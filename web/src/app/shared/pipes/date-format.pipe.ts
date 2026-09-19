import { Pipe, PipeTransform } from '@angular/core';

/**
 * Property-local date formatting.
 *
 * Times in SpMS are always shown in the property's timezone, never the
 * viewer's, because a scheduler in one city routinely works a board in
 * another. The zone is passed in rather than inferred from the browser.
 */
@Pipe({ name: 'propertyDate', standalone: true })
export class DateFormatPipe implements PipeTransform {
  transform(
    value: Date | string | number,
    style: 'date' | 'time' | 'datetime' | 'short' = 'datetime',
    timeZone = 'America/New_York',
  ): string {
    const d = value instanceof Date ? value : new Date(value);
    if (Number.isNaN(d.getTime())) return '—';

    const opts: Intl.DateTimeFormatOptions =
      style === 'date'  ? { day: '2-digit', month: 'short', year: 'numeric' }
    : style === 'time'  ? { hour: 'numeric', minute: '2-digit' }
    : style === 'short' ? { day: '2-digit', month: 'short' }
    :                     { day: '2-digit', month: 'short', hour: 'numeric', minute: '2-digit' };

    return new Intl.DateTimeFormat('en-GB', { ...opts, timeZone }).format(d);
  }
}
