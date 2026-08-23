import { Routes } from '@angular/router';
import { Home } from './home';
import { Orders } from './orders';

export const routes: Routes = [
  { path: '', component: Home },
  { path: 'orders', component: Orders },
];
