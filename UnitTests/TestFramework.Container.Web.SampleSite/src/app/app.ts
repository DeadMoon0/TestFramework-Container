import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <h1 data-testid="title">Sample Site</h1>
    <nav>
      <a routerLink="/" data-testid="nav-home">Home</a>
      <a routerLink="/orders" data-testid="nav-orders">Orders</a>
    </nav>
    <router-outlet />
  `,
})
export class App {}
