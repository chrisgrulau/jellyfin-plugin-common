using System;
using Jellyfin.Plugin.Common.Resilience;
using Xunit;

namespace Jellyfin.Plugin.Common.Tests;

public class BackoffScheduleTests
{
    [Theory]
    [InlineData((int)FailureClass.Authentication)]
    [InlineData((int)FailureClass.BadRequest)]
    public void Configuration_and_request_errors_are_never_retried(int failure)
        => Assert.Null(BackoffSchedule.Delay((FailureClass)failure, 1, TimeSpan.FromMinutes(5), 0.5));

    [Fact]
    public void A_delay_stated_by_the_provider_wins_with_a_little_spread()
    {
        Assert.Equal(TimeSpan.FromHours(9), BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.FromHours(9), 0));
        Assert.Equal(TimeSpan.FromHours(9.45), BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.FromHours(9), 0.5));
    }

    [Fact]
    public void A_tiny_stated_delay_waits_at_least_a_second()
        => Assert.Equal(BackoffSchedule.ProviderStatedMin, BackoffSchedule.Delay(FailureClass.Transient, 1, TimeSpan.FromTicks(1), 0));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.999)]
    public void A_huge_stated_delay_is_capped_without_overflowing(double jitter)
    {
        Assert.Equal(BackoffSchedule.ProviderStatedMax, BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.MaxValue, jitter));
        Assert.Equal(BackoffSchedule.ProviderStatedMax, BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.FromDays(3650), jitter));
        _ = DateTime.UtcNow + BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.MaxValue, jitter)!.Value;
    }

    [Fact]
    public void Zero_or_negative_stated_delays_use_the_schedule()
    {
        var own = BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, null, 0.5);
        Assert.Equal(own, BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.Zero, 0.5));
        Assert.Equal(own, BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.FromSeconds(-5), 0.5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    public void Bad_jitter_is_refused(double jitter)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BackoffSchedule.Delay(FailureClass.Transient, 1, null, jitter));

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 24.0)]
    [InlineData(3, 72.0)]
    [InlineData(4, 168.0)]
    [InlineData(9, 168.0)]
    public void Provider_limits_back_off_from_an_hour_to_weekly(int attempt, double hours)
    {
        var d = BackoffSchedule.Delay(FailureClass.ProviderLimit, attempt, null, 0.5)!.Value;
        Assert.Equal(hours, d.TotalHours, precision: 6);
    }

    [Fact]
    public void Provider_limit_jitter_stays_within_ten_percent()
    {
        var low = BackoffSchedule.Delay(FailureClass.ProviderLimit, 2, null, 0)!.Value;
        var high = BackoffSchedule.Delay(FailureClass.ProviderLimit, 2, null, 0.999999)!.Value;
        Assert.InRange(low.TotalHours, 21.5, 22.0);
        Assert.InRange(high.TotalHours, 26.0, 26.5);
    }

    [Fact]
    public void Transient_retries_grow_quickly_but_stay_capped()
    {
        var first = BackoffSchedule.Delay(FailureClass.Transient, 1, null, 0.999999)!.Value;
        var late = BackoffSchedule.Delay(FailureClass.Transient, 30, null, 0.999999)!.Value;
        Assert.True(first <= BackoffSchedule.TransientFirst);
        Assert.True(late <= BackoffSchedule.TransientCap);
        Assert.True(late > TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void Without_a_connection_it_just_keeps_checking()
        => Assert.Equal(BackoffSchedule.ConnectivityProbeInterval, BackoffSchedule.Delay(FailureClass.NoConnection, 50, null, 0.3));
}
