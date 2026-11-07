import { CanActivateFn } from '@angular/router';

export const authGuard: CanActivateFn = () => localStorage.getItem('token') !== null;
