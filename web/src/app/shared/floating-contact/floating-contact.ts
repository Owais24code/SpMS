import { Component, ChangeDetectionStrategy, signal, HostListener } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-floating-contact',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './floating-contact.html',
  styleUrl: './floating-contact.scss',
})
export class FloatingContact {
  protected readonly open = signal(false);

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.open()) this.open.set(false);
  }

  protected toggle(): void {
    this.open.update((v) => !v);
  }
}
