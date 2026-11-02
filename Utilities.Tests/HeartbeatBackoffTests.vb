Option Strict On
Option Explicit On

Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-238 - back-off of the MainForm heartbeat after a lost database connection.
<TestClass>
Public Class HeartbeatBackoffTests

    Private Shared ReadOnly T0 As New DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc)

    <TestMethod>
    Public Sub NoFailure_DoesNotSkip()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))
        Assert.IsFalse(backoff.ShouldSkip(T0))
    End Sub

    <TestMethod>
    Public Sub FirstFailure_IsReported_LaterOnesAreNot_UntilASuccess()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))

        Assert.IsTrue(backoff.Failed(T0), "first failure")
        Assert.IsFalse(backoff.Failed(T0.AddSeconds(31)), "second failure, same outage")
        backoff.Succeeded()
        Assert.IsTrue(backoff.Failed(T0.AddSeconds(90)), "first failure of a new outage")
    End Sub

    <TestMethod>
    Public Sub AfterFailure_SkipsForThePeriod_ThenTriesAgain()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))
        backoff.Failed(T0)

        Assert.IsTrue(backoff.ShouldSkip(T0.AddSeconds(29)))
        Assert.IsFalse(backoff.ShouldSkip(T0.AddSeconds(30)))
    End Sub

    <TestMethod>
    Public Sub Success_EndsTheBackoff()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))
        backoff.Failed(T0)
        backoff.Succeeded()

        Assert.IsFalse(backoff.ShouldSkip(T0.AddSeconds(1)))
    End Sub

    <TestMethod>
    Public Sub IsConnectionFailure_ConnectionNumbersAndTimeout_True()
        For Each number As Integer In New Integer() {-2, -1, 53, 11001, 10065}
            Assert.IsTrue(HeartbeatBackoff.IsConnectionFailure(
                New InvalidOperationException("wrapper", SqlExceptionFactory.Create(number))), number.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub IsConnectionFailure_OtherErrors_False()
        Assert.IsFalse(HeartbeatBackoff.IsConnectionFailure(SqlExceptionFactory.Create(547)))
        Assert.IsFalse(HeartbeatBackoff.IsConnectionFailure(New InvalidOperationException("boom")))
    End Sub

End Class
