import { Component } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule],
  template: `
    <h1>Logga in</h1>
    <form [formGroup]="form" (ngSubmit)="submit()">
      <label for="email">E-post</label>
      <input id="email" type="email" formControlName="email" />

      <label for="password">Lösenord</label>
      <input id="password" type="password" formControlName="password" />

      <button type="submit">Logga in</button>
    </form>
  `,
})
export class LoginComponent {
  form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  constructor(private fb: FormBuilder, private http: HttpClient) {}

  submit() {
    this.http.post('/api/auth/login', this.form.value).subscribe();
  }
}
