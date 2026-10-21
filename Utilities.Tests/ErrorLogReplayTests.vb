Option Strict On
Option Explicit On

Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-238 - ErrorLog.ReplayPending: queued entries written to the database after Configure.
<TestClass>
Public Class ErrorLogReplayTests

    ' DataSource "." and InitialCatalog "x": queued lines for ./x belong to this connection.
    Private Const ConnectionString As String = "Data Source=.;Initial Catalog=x;Integrated Security=True"
    Private Shared ReadOnly CreatedUtc As New DateTime(2026, 10, 20, 14, 35, 12, DateTimeKind.Utc)

    Private _writer As FakeWriter
    Private _presenter As FakePresenter
    Private _fallback As FakeFallback
    Private _queue As FakePendingQueue

    <TestInitialize>
    Public Sub Init()
        Rewire()
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        Assert.AreEqual(0, _presenter.CallCount, "replay must never show")
        ErrorLog.Reset()
    End Sub

    Private Sub Rewire()
        ErrorLog.Reset()
        _writer = New FakeWriter()
        _presenter = New FakePresenter()
        _fallback = New FakeFallback()
        _queue = New FakePendingQueue()
        ErrorLog.SetSeams(_writer, _presenter, _fallback, _queue)
    End Sub

    Private Shared Sub ConfigureLogging()
        ErrorLog.Configure(ConnectionString, "UPLUS", "9.8.7")
    End Sub

    Private Function AddLine(localRef As String, Optional server As String = ".",
                             Optional database As String = "x") As PendingLine
        Dim line As New PendingLine With {
            .Raw = "raw " & localRef,
            .Entry = New ErrorLogEntry With {
                .App = "UPLUS", .Message = "m " & localRef, .LocalRef = localRef, .CreatedUtc = CreatedUtc,
                .TargetServer = server, .TargetDatabase = database}}
        _queue.AddLine(line)
        Return line
    End Function

    Private Sub ThrowForFirst(ex As Exception)
        _writer.OnWrite = Sub(e)
                              If e.LocalRef = "F1" Then Throw ex
                          End Sub
    End Sub

    <TestMethod>
    Public Sub Replay_WritesMatchingLinesAndRemovesThem()
        ConfigureLogging()
        Dim l1 As PendingLine = AddLine("F1")
        Dim l2 As PendingLine = AddLine("F2")

        ErrorLog.ReplayPending()

        Assert.AreEqual(2, _writer.CallCount)
        Assert.AreEqual("F1", _writer.Entries(0).LocalRef)
        Assert.AreEqual("F2", _writer.Entries(1).LocalRef)
        Assert.AreEqual(CreatedUtc, _writer.Entries(0).CreatedUtc)
        Assert.AreEqual(CreatedUtc, _writer.Entries(1).CreatedUtc)
        Assert.AreEqual(1, _queue.RemoveCalls.Length)
        CollectionAssert.AreEqual({l1, l2}, _queue.RemoveCalls(0).Item1)
        Assert.AreEqual(0, _queue.RemoveCalls(0).Item2.Length)
        Assert.AreEqual(0, _queue.Entries.Length)
    End Sub

    <TestMethod>
    Public Sub Replay_OtherDatabaseOnly_WritesNothingAndKeepsFile()
        ConfigureLogging()
        AddLine("F1", "prod", "UPLUS2")
        AddLine("F2", "prod", "UPLUS2")

        ErrorLog.ReplayPending()

        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(0, _queue.RemoveCalls.Length)
        Assert.AreEqual(2, _queue.Entries.Length)
    End Sub

    <TestMethod>
    Public Sub Replay_MixedTargets_OnlyMatchingLinesAreRemoved()
        ConfigureLogging()
        AddLine("F0", "prod", "UPLUS2")
        Dim mine As PendingLine = AddLine("F1")

        ErrorLog.ReplayPending()

        Assert.AreEqual(1, _writer.CallCount)
        CollectionAssert.AreEqual({mine}, _queue.RemoveCalls(0).Item1)
        Assert.AreEqual("F0", _queue.Entries.Single().LocalRef)
    End Sub

    ' 8144 (too many arguments) and 2812 (no such proc): the database still runs an older
    ' p_insert_error_log. The line must wait for the uo-sql publish, not be rejected.
    <TestMethod>
    Public Sub Replay_ConnectionOrProcError_StopsAndKeepsRest()
        For Each c As Tuple(Of Integer, String) In {
                Tuple.Create(53, "database unreachable"),
                Tuple.Create(8144, "p_insert_error_log cannot take it yet"),
                Tuple.Create(2812, "p_insert_error_log cannot take it yet")}
            Rewire()
            ConfigureLogging()
            AddLine("F1")
            AddLine("F2")
            ThrowForFirst(SqlExceptionFactory.Create(c.Item1))

            ErrorLog.ReplayPending()

            Dim label As String = c.Item1.ToString()
            Assert.AreEqual(1, _writer.CallCount, label)
            Assert.AreEqual(0, _queue.RemoveCalls.Length, label)
            Assert.AreEqual(2, _queue.Entries.Length, label)
            ' Debug is compiled out of Release: the stop must be in the fallback file.
            Assert.AreEqual(1, _fallback.Written.Count(Function(w) w.Contains("Replay stopped at local code F1, " & c.Item2)), label)
        Next
    End Sub

    <TestMethod>
    Public Sub Replay_ConnectionErrorAfterSuccess_RemovesOnlyProcessed()
        ConfigureLogging()
        Dim l0 As PendingLine = AddLine("F0")
        AddLine("F1")
        AddLine("F2")
        ThrowForFirst(SqlExceptionFactory.Create(-1))

        ErrorLog.ReplayPending()

        Assert.AreEqual(2, _writer.CallCount)
        Assert.AreEqual(1, _queue.RemoveCalls.Length)
        CollectionAssert.AreEqual({l0}, _queue.RemoveCalls(0).Item1)
        Assert.AreEqual(0, _queue.RemoveCalls(0).Item2.Length)
        Assert.AreEqual(2, _queue.Entries.Length)
        Assert.AreEqual(1, _fallback.Written.Count(Function(w) w.Contains("database unreachable")))
    End Sub

    <TestMethod>
    Public Sub Replay_OtherError_RejectsThatLineAndContinues()
        ConfigureLogging()
        Dim l1 As PendingLine = AddLine("F1")
        Dim l2 As PendingLine = AddLine("F2")
        ThrowForFirst(SqlExceptionFactory.Create(50000))

        ErrorLog.ReplayPending()

        Assert.AreEqual(2, _writer.CallCount)
        Assert.AreEqual(1, _queue.RemoveCalls.Length)
        CollectionAssert.AreEqual({l2}, _queue.RemoveCalls(0).Item1)
        CollectionAssert.AreEqual({l1}, _queue.RemoveCalls(0).Item2)
        Assert.AreEqual(0, _queue.Entries.Length)
    End Sub

    <TestMethod>
    Public Sub Replay_NoRowOrNoPermission_RejectsThatLine()
        For Each failure As Exception In New Exception() {
                New InvalidOperationException("p_insert_error_log returned no row."),
                SqlExceptionFactory.Create(229)}
            Rewire()
            ConfigureLogging()
            Dim l1 As PendingLine = AddLine("F1")
            ThrowForFirst(failure)

            ErrorLog.ReplayPending()

            CollectionAssert.AreEqual({l1}, _queue.RemoveCalls.Single().Item2, failure.Message)
        Next
    End Sub

    <TestMethod>
    Public Sub Replay_CorruptLine_IsRejected()
        ConfigureLogging()
        Dim corrupt As New PendingLine With {.Raw = "{not json", .Entry = Nothing}
        _queue.AddLine(corrupt)
        Dim good As PendingLine = AddLine("F1")

        ErrorLog.ReplayPending()

        Assert.AreEqual(1, _writer.CallCount)
        CollectionAssert.AreEqual({good}, _queue.RemoveCalls.Single().Item1)
        CollectionAssert.AreEqual({corrupt}, _queue.RemoveCalls.Single().Item2)
    End Sub

    <TestMethod>
    Public Sub Replay_WhenNotEnabled_DoesNothing()
        ' Writer, queue and target are all set, so only the enabled check can stop the write.
        ConfigureLogging()
        ErrorLog.Disable()
        AddLine("F1")

        ErrorLog.ReplayPending()

        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(0, _queue.RemoveCalls.Length)
    End Sub

    <TestMethod>
    Public Sub Replay_WhileRunning_SecondCallRunsAgainAfterwards()
        ConfigureLogging()
        AddLine("F1")
        Dim nested As Integer = 0
        Dim callsInNested As Integer = -1
        _writer.OnWrite = Sub(e)
                              nested += 1
                              If nested = 1 Then
                                  ' A line queued and a Configure during the replay.
                                  AddLine("F2")
                                  ErrorLog.ReplayPending()
                                  callsInNested = _writer.CallCount
                              End If
                          End Sub

        ErrorLog.ReplayPending()

        Assert.AreEqual(1, callsInNested, "the nested call returned at once without writing")
        Assert.AreEqual(2, _writer.CallCount, "the replay ran once more for F2")
        Assert.AreEqual("F2", _writer.Entries(1).LocalRef)
        Assert.AreEqual(2, _queue.RemoveCalls.Length)
        Assert.AreEqual(0, _queue.Entries.Length)

        ' The flags are released afterwards: a new line is replayed by the next call, once.
        AddLine("F3")
        ErrorLog.ReplayPending()
        Assert.AreEqual(3, _writer.CallCount)
        Assert.AreEqual(3, _queue.RemoveCalls.Length)
    End Sub

End Class
