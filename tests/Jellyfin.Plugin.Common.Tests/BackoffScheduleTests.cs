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
    public void A_delay_stated_by_the_provider_wins()
        => Assert.Equal(TimeSpan.FromHours(9), BackoffSchedule.Delay(FailureClass.ProviderLimit, 1, TimeSpan.FromHours(9), 0.5));

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
