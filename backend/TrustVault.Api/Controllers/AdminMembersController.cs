using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Models.Admin;
using TrustVault.Api.Services;
using TrustVault.Domain.Audit;
using TrustVault.Domain.Documents;
using TrustVault.Domain.Security;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/admin/members")]
[Authorize(Roles = UserRoles.Administrator)]
public sealed class AdminMembersController(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    ApplicationDbContext database,
    DocumentAuditWriter auditWriter) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MemberResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MemberResponse>>> List(
        CancellationToken cancellationToken)
    {
        var members = await database.Users
            .AsNoTracking()
            .Where(user => !user.IsPrimaryAdministrator)
            .OrderBy(user => user.Email)
            .ToListAsync(cancellationToken);

        var responses = new List<MemberResponse>(members.Count);
        foreach (var member in members)
        {
            responses.Add(new MemberResponse(
                member.Id.ToString(),
                member.Email ?? string.Empty,
                (await userManager.GetRolesAsync(member)).ToArray(),
                member.IsSuspended,
                member.CreatedAtUtc));
        }

        return Ok(responses);
    }

    [HttpPost]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [ProducesResponseType<MemberCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        CreateMemberRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var existingMember = await userManager.FindByEmailAsync(email);
        if (existingMember is not null && !existingMember.IsSuspended)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "An account with this email already exists."
            });
        }

        if (!await roleManager.RoleExistsAsync(UserRoles.Viewer))
        {
            throw new InvalidOperationException("The configured Viewer role does not exist.");
        }

        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var member = existingMember ?? new ApplicationUser
        {
            UserName = email,
            Email = email,
            CreatedAtUtc = DateTime.UtcNow,
            MustChangePassword = true
        };
        if (existingMember is null)
        {
            var createResult = await userManager.CreateAsync(member, temporaryPassword);
            if (!createResult.Succeeded)
            {
                if (createResult.Errors.Any(error =>
                    error.Code is nameof(IdentityErrorDescriber.DuplicateEmail)
                        or nameof(IdentityErrorDescriber.DuplicateUserName)))
                {
                    return Conflict(new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "An account with this email already exists."
                    });
                }

                return BadRequest(ToValidationProblem(createResult.Errors));
            }
        }
        else
        {
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(member);
            var resetResult = await userManager.ResetPasswordAsync(
                member,
                resetToken,
                temporaryPassword);
            if (!resetResult.Succeeded)
            {
                return BadRequest(ToValidationProblem(resetResult.Errors));
            }

            member.IsSuspended = false;
            member.MustChangePassword = true;
            member.AccessFailedCount = 0;
            member.LockoutEnd = null;
            var updateResult = await userManager.UpdateAsync(member);
            if (!updateResult.Succeeded)
            {
                return BadRequest(ToValidationProblem(updateResult.Errors));
            }
        }

        if (!await userManager.IsInRoleAsync(member, UserRoles.Viewer))
        {
            var roleResult = await userManager.AddToRoleAsync(member, UserRoles.Viewer);
            if (!roleResult.Succeeded)
            {
                return BadRequest(ToValidationProblem(roleResult.Errors));
            }
        }

        await auditWriter.WriteAsync(
            HttpContext,
            null,
            existingMember is null ? "member.created" : "member.reactivated",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                userId = member.Id,
                email = member.Email,
                role = UserRoles.Viewer
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Created(
            "/api/admin/members",
            new MemberCreatedResponse(
                member.Id.ToString(),
                member.Email!,
                UserRoles.Viewer,
                temporaryPassword,
                true));
    }

    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var member = await userManager.FindByIdAsync(userId.ToString());
        if (member is null || member.IsPrimaryAdministrator)
        {
            return NotFound();
        }

        if (member.IsSuspended)
        {
            return NoContent();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        member.IsSuspended = true;
        member.RefreshTokenHash = null;
        member.RefreshTokenExpiresAtUtc = null;
        member.RefreshTokenIssuedAtUtc = null;
        member.SecurityStamp = Guid.NewGuid().ToString();
        var updateResult = await userManager.UpdateAsync(member);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Unable to deactivate the member: " +
                string.Join("; ", updateResult.Errors.Select(error => error.Code)));
        }

        var activeGrants = await database.DocumentGrants
            .Where(grant => grant.RevokedAtUtc == null
                && (grant.UserId == userId
                    || database.Documents.Any(document =>
                        document.Id == grant.DocumentId && document.OwnerId == userId)))
            .ToListAsync(cancellationToken);
        foreach (var grant in activeGrants)
        {
            grant.RevokedAtUtc = now;
            grant.RevocationReason = "Member account deactivated by administrator";
        }

        await database.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            HttpContext,
            null,
            "member.deactivated",
            AuditResult.Success,
            JsonSerializer.Serialize(new
            {
                userId = member.Id,
                email = member.Email,
                revokedGrantCount = activeGrants.Count
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private static ValidationProblemDetails ToValidationProblem(
        IEnumerable<IdentityError> errors) =>
        new(errors
            .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal)
                ? "password"
                : "account")
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Description).ToArray()));
}
