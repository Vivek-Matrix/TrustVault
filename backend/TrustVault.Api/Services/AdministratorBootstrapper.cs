using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Api.Services;

public static class AdministratorBootstrapper
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var database = serviceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var role in new[]
                 {
                     UserRoles.Administrator,
                     UserRoles.Viewer
                 })
        {
            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(role));
            if (!roleResult.Succeeded && !await roleManager.RoleExistsAsync(role))
            {
                throw new InvalidOperationException(
                    $"Unable to initialize the {role} role: " +
                    string.Join("; ", roleResult.Errors.Select(error => error.Code)));
            }
        }

        var administrators = await userManager.GetUsersInRoleAsync(UserRoles.Administrator);
        if (administrators.Count > 1)
        {
            throw new InvalidOperationException(
                "TrustVault supports exactly one administrator account; multiple administrator memberships were found.");
        }

        if (administrators.Count == 1)
        {
            var administrator = administrators[0];
            if (!administrator.IsPrimaryAdministrator)
            {
                administrator.IsPrimaryAdministrator = true;
                var updateResult = await userManager.UpdateAsync(administrator);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Unable to mark the existing administrator as the primary administrator: " +
                        string.Join("; ", updateResult.Errors.Select(error => error.Code)));
                }
            }

            return;
        }

        var email = configuration["AdminBootstrap:Email"]?.Trim();
        var password = configuration["AdminBootstrap:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "No administrator exists. Configure AdminBootstrap:Email and AdminBootstrap:Password using deployment secrets before starting the API.");
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException(
                "The configured bootstrap administrator email is already used by a non-administrator account. Choose a dedicated administrator email.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            CreatedAtUtc = DateTime.UtcNow,
            IsPrimaryAdministrator = true
        };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Unable to create the bootstrap administrator: " +
                string.Join("; ", createResult.Errors.Select(error => error.Code)));
        }

        var addRoleResult = await userManager.AddToRoleAsync(user, UserRoles.Administrator);
        if (!addRoleResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Unable to assign the bootstrap administrator role: " +
                string.Join("; ", addRoleResult.Errors.Select(error => error.Code)));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
