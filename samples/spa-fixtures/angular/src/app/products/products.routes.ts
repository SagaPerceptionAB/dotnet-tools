import { Routes } from '@angular/router';
import { authGuard } from '../auth.guard';
import { ProductListComponent } from './product-list.component';
import { ProductDetailComponent } from './product-detail.component';
import { ProductFormComponent } from './product-form.component';

export const PRODUCT_ROUTES: Routes = [
  { path: '', component: ProductListComponent },
  { path: 'new', component: ProductFormComponent, canActivate: [authGuard] },
  { path: ':id', component: ProductDetailComponent },
  { path: ':id/edit', component: ProductFormComponent, canActivate: [authGuard] },
];
