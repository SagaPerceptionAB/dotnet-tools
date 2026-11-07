import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './product-list.component.html',
})
export class ProductListComponent {
  products: { id: number; name: string }[] = [];

  constructor(private http: HttpClient) {
    this.http.get<{ id: number; name: string }[]>('/api/products').subscribe((p) => (this.products = p));
  }
}
