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

    ' The auto-logout read is gated on IsFailing: it stays True after the period has passed (the
    ' semaphore read may retry, the auto-logout read waits for that retry to succeed).
    <TestMethod>
    Public Sub IsFailing_FromFailureUntilSuccess_AlsoAfterThePeriod()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))
        Assert.IsFalse(backoff.IsFailing, "no failure yet")

        backoff.Failed(T0)
        Assert.IsFalse(backoff.ShouldSkip(T0.AddMinutes(4)))
        Assert.IsTrue(backoff.IsFailing, "period passed, no success yet")

        backoff.Succeeded()
        Assert.IsFalse(backoff.IsFailing)
    End Sub

    <TestMethod>
    Public Sub IsConnectionFailure_ConnectionNumbersAndTimeout_True()
        For Each number As Integer In New Integer() {-2, -1, 5, 53, 11001, 10065}
            Assert.IsTrue(HeartbeatBackoff.IsConnectionFailure(
                New InvalidOperationException("wrapper", SqlExceptionFactory.Create(number))), number.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub IsConnectionFailure_OtherErrors_False()
        Assert.IsFalse(HeartbeatBackoff.IsConnectionFailure(SqlExceptionFactory.Create(547)))
        Assert.IsFalse(HeartbeatBackoff.IsConnectionFailure(New InvalidOperationException("boom")))
    End Sub

    ' OnFailure is the catch of both the heartbeat and the auto-logout read: whichever sees the first
    ' failure of an outage reports it, without a write to the database.
    <TestMethod>
    Public Sub OnFailure_FirstFailureOnEitherPath_ReportedOnceWithoutWrite()
        Dim writer As New FakeWriter()
        Dim presenter As New FakePresenter()
        Dim queue As New FakePendingQueue()
        ErrorLog.Reset()
        ErrorLog.SetSeams(writer, presenter, New FakeFallback(), queue)
        ErrorLog.Configure("Data Source=.;Initial Catalog=x;Integrated Security=True", "UPLUS", "9.8.7")
        Try
            Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))

            Assert.IsTrue(backoff.OnFailure(SqlExceptionFactory.Create(-2), T0, "MainForm.TimerInMainThread"))
            Assert.IsTrue(backoff.OnFailure(SqlExceptionFactory.Create(-2), T0.AddSeconds(31), "MainForm.CheckSemaphoreTable"))

            Assert.AreEqual(1, presenter.CallCount, "reported once per outage")
            StringAssert.StartsWith(presenter.Shown(0), ErrorLog.ConnectionFailedText)
            Assert.AreEqual(0, writer.CallCount)
            Assert.AreEqual(1, queue.Entries.Length)
            Assert.IsTrue(backoff.ShouldSkip(T0.AddSeconds(32)))
        Finally
            ErrorLog.Reset()
        End Try
    End Sub

    <TestMethod>
    Public Sub OnFailure_OtherError_FalseAndNoBackoff()
        Dim backoff As New HeartbeatBackoff(TimeSpan.FromSeconds(30))

        Assert.IsFalse(backoff.OnFailure(SqlExceptionFactory.Create(547), T0, "ctx"))
        Assert.IsFalse(backoff.ShouldSkip(T0.AddSeconds(1)))
    End Sub

End Class
