import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

interface SiteConfig {
  apiBaseUrl?: string | null;
}

interface Order {
  id: number;
  name: string;
  quantity: number;
}

@Component({
  selector: 'app-orders',
  template: `
    <h2 data-testid="route-orders">route:orders</h2>
    <p data-testid="api-base-url">{{ apiBaseUrl() ?? '(same origin)' }}</p>
    @if (error()) {
      <p data-testid="orders-error">orders unavailable</p>
    }
    <ul data-testid="orders">
      @for (order of orders(); track order.id) {
        <li>{{ order.name }} ({{ order.quantity }})</li>
      }
    </ul>
  `,
})
export class Orders implements OnInit {
  private readonly http = inject(HttpClient);

  readonly apiBaseUrl = signal<string | null>(null);
  readonly orders = signal<Order[]>([]);
  readonly error = signal(false);

  async ngOnInit(): Promise<void> {
    // The runtime configuration decides where the backend lives: same-origin behind the site's own
    // proxy when apiBaseUrl is null, or an absolute address the environment wrote into the file.
    let config: SiteConfig = {};
    try {
      config = await firstValueFrom(this.http.get<SiteConfig>('assets/config.json'));
    } catch {
      // A missing config file means same-origin, which is also the checked-in default.
    }

    this.apiBaseUrl.set(config.apiBaseUrl ?? null);

    try {
      const orders = await firstValueFrom(this.http.get<Order[]>(`${config.apiBaseUrl ?? ''}/api/orders`));
      this.orders.set(orders);
    } catch {
      this.error.set(true);
    }
  }
}
