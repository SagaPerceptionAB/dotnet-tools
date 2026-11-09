import { Component } from '@angular/core';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { ProductService } from './product.service';

@Component({
  selector: 'app-product-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './product-form.component.html',
})
export class ProductFormComponent {
  form = this.fb.group({
    name: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(60)]],
    sku: ['', [Validators.required, Validators.pattern('^[A-Z]{3}-\\d{4}$')]],
    price: [0, [Validators.required, Validators.min(0), Validators.max(100000)]],
    contact: new FormControl('', [Validators.email]),
  });

  constructor(private fb: FormBuilder, private products: ProductService) {}

  save() {
    this.products.create(this.form.value as never).subscribe();
  }
}
