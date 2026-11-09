import { Routes } from '@angular/router';
import { HomeComponent } from './home.component';
import { NotFoundComponent } from './not-found.component';
import { ProfileComponent } from './account/profile.component';
import { SettingsComponent } from './account/settings.component';
import { authGuard } from './auth.guard';

export const routes: Routes = [
  { path: '', component: HomeComponent },
  { path: 'home', redirectTo: '', pathMatch: 'full' },
  {
    path: 'login',
    loadComponent: () => import('./account/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'products',
    loadChildren: () => import('./products/products.routes').then((m) => m.PRODUCT_ROUTES),
  },
  {
    path: 'account',
    canActivate: [authGuard],
    children: [
      { path: '', component: ProfileComponent },
      { path: 'settings', component: SettingsComponent },
    ],
  },
  { path: '**', component: NotFoundComponent },
];
