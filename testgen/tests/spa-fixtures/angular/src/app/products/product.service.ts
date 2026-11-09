import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface Product {
  id: number;
  name: string;
}

@Injectable({ providedIn: 'root' })
export class ProductService {
  constructor(private http: HttpClient) {}

  list() {
    return this.http.get<Product[]>('/api/products');
  }

  get(id: number) {
    return this.http.get<Product>(`/api/products/${id}`);
  }

  create(product: Partial<Product>) {
    return this.http.post<Product>('/api/products', product);
  }

  update(id: number, product: Partial<Product>) {
    return this.http.put<Product>(`/api/products/${id}`, product);
  }

  remove(id: number) {
    return this.http.delete(`/api/products/${id}`);
  }
}
