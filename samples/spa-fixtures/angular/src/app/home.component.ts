import { Component, OnInit } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-home',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './home.component.html',
})
export class HomeComponent implements OnInit {
  featured = { id: 7 };

  constructor(private title: Title) {}

  ngOnInit() {
    this.title.setTitle('Start');
  }
}
