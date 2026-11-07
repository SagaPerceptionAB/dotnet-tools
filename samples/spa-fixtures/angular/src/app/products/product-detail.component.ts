import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ProductService } from './product.service';

@Component({
  selector: 'app-product-detail',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './product-detail.component.html',
})
export class ProductDetailComponent {
  @Input() id = 0;
  product$ = this.products.get(this.id);

  constructor(private products: ProductService) {}
}
