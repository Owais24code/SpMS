import { Component, ChangeDetectionStrategy, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { inject } from '@angular/core';

type SubmitState = 'idle' | 'submitting' | 'success' | 'error';

@Component({
  selector: 'app-contact',
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './contact.html',
  styleUrl: './contact.scss',
})
export class Contact {
  private readonly fb = inject(FormBuilder);

  protected readonly state = signal<SubmitState>('idle');

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(180)]],
    property: ['', [Validators.maxLength(120)]],
    message: ['', [Validators.required, Validators.minLength(10), Validators.maxLength(2000)]],
  });

  protected hasError(field: keyof typeof this.form.controls): boolean {
    const c = this.form.controls[field];
    return c.invalid && (c.touched || c.dirty);
  }

  protected errorFor(field: keyof typeof this.form.controls): string {
    const c = this.form.controls[field];
    if (c.hasError('required')) return 'This field is required.';
    if (c.hasError('email')) return 'Enter an email address in the form name@company.com.';
    if (c.hasError('minlength')) return 'Please give us a little more detail — at least 10 characters.';
    if (c.hasError('maxlength')) return 'This is longer than we can accept.';
    return 'Please check this field.';
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      // Move focus to the first field in error so keyboard and screen-reader
      // users are taken to the problem rather than left at the button.
      queueMicrotask(() => {
        const el = document.querySelector<HTMLElement>('[aria-invalid="true"]');
        el?.focus();
      });
      return;
    }

    this.state.set('submitting');
    try {
      // Wire to the SpMS API when the endpoint exists.
      await new Promise((r) => setTimeout(r, 700));
      this.state.set('success');
      this.form.reset();
    } catch {
      this.state.set('error');
    }
  }
}
