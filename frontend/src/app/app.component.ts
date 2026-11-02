import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
    selector: 'app-root',
    imports: [RouterOutlet],
    template: `
    <main>
      <h1>{{ title }}</h1>
      <p>Private document sharing with per-document access controls.</p>
      <router-outlet />
    </main>
  `,
    styleUrl: './app.component.scss'
})
export class AppComponent {
  title = 'TrustVault';
}
