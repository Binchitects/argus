import { describe as group, expect, it } from 'vitest'
import { blankSchedule, cronOf, describe, scheduleOf } from './tasks-schedule'

group("a task's schedule", () => {
  it('becomes cron from the choices', () => {
    expect(cronOf({ ...blankSchedule, repeat: 'daily', time: '07:30' })).toBe('30 7 * * *')
    expect(cronOf({ ...blankSchedule, repeat: 'weekdays', time: '09:00' })).toBe('0 9 * * 1-5')
    expect(cronOf({ ...blankSchedule, repeat: 'weekly', time: '08:15', day: 7 })).toBe('15 8 * * 0')
    expect(cronOf({ ...blankSchedule, repeat: 'monthly', time: '06:00', date: 1 })).toBe('0 6 1 * *')
    expect(cronOf({ ...blankSchedule, repeat: 'hours', time: '00:05', hours: 4 })).toBe('5 */4 * * *')
    expect(cronOf({ ...blankSchedule, repeat: 'minutes', minutes: 15 })).toBe('*/15 * * * *')
  })

  it('reads back to the choices, and in words', () => {
    for (const cron of ['30 7 * * *', '0 9 * * 1-5', '15 8 * * 0', '0 6 1 * *', '5 */4 * * *', '*/15 * * * *']) expect(cronOf(scheduleOf(cron))).toBe(cron)
    expect(describe('*/15 * * * *')).toBe('Every 15 minutes')
    expect(describe('0 9 * * 1-5')).toBe('Weekdays at 09:00')
    expect(describe('15 8 * * 0')).toBe('Sundays at 08:15')
    expect(describe('0 6 1 * *')).toBe('On day 1 of each month at 06:00')
    expect(describe('0 */1 * * *')).toBe('Every hour')
    expect(describe('0 9 1,15 * *')).toBe('Cron: 0 9 1,15 * *')
  })
})
