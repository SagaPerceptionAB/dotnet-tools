import { createRouter, createWebHistory } from 'vue-router';
import HomeView from '../views/HomeView.vue';
import ProductsView from '../views/ProductsView.vue';
import AdminLayout from '../views/AdminLayout.vue';
import AdminHome from '../views/AdminHome.vue';
import NotFoundView from '../views/NotFoundView.vue';

const routes = [
  { path: '/', name: 'home', component: HomeView },
  { path: '/home', redirect: '/' },
  { path: '/about', name: 'about', component: () => import('../views/AboutView.vue') },
  { path: '/products', name: 'products', component: ProductsView },
  { path: '/products/:id', name: 'product', component: () => import('@/views/ProductDetailView.vue') },
  { path: '/login', name: 'login', component: () => import('../views/LoginView.vue') },
  {
    path: '/admin',
    component: AdminLayout,
    meta: { requiresAuth: true },
    children: [
      { path: '', component: AdminHome },
      { path: 'users', component: () => import('../views/AdminUsers.vue') },
    ],
  },
  { path: '/:pathMatch(.*)*', name: 'NotFound', component: NotFoundView },
];

const router = createRouter({
  history: createWebHistory(),
  routes,
});

router.beforeEach((to) => {
  if (to.meta.requiresAuth && !localStorage.getItem('token')) return '/login';
});

export default router;
