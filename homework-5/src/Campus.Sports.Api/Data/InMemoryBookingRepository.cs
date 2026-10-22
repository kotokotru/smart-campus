using System.Collections.Concurrent;
using Campus.Sports.Api.Domain;

namespace Campus.Sports.Api.Data;

/// <summary>
/// Репозиторий в памяти — для локального запуска без PostgreSQL и для CI-прогона sanity-теста
/// (ConnectionStrings:Campus не задана). В production не используется.
/// </summary>
public sealed class InMemoryBookingRepository : IBookingRepository
{
    private readonly ConcurrentDictionary<Guid, SportsBooking> _store = new();
    private readonly object _gate = new();

    public Task<SportsBooking?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue(id, out var b) ? b : null);

    public Task CreateAsync(SportsBooking booking, CancellationToken ct)
    {
        lock (_gate)
        {
            var conflict = _store.Values.Any(b =>
                b.FacilityId == booking.FacilityId &&
                b.Status == BookingStatus.Active &&
                b.TimeRange.Overlaps(booking.TimeRange));
            if (conflict) throw new BookingConflictException(booking.FacilityId, booking.TimeRange);

            _store[booking.Id] = booking;
        }
        return Task.CompletedTask;
    }

    public Task<bool> CancelOrPurgeAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_store.TryGetValue(id, out var b)) return Task.FromResult(false);
            if (b.IsSanityTest) return Task.FromResult(_store.TryRemove(id, out _));
            if (b.Status == BookingStatus.Cancelled) return Task.FromResult(false);
            _store[id] = b with { Status = BookingStatus.Cancelled };
            return Task.FromResult(true);
        }
    }

    internal int Count => _store.Count;
}
