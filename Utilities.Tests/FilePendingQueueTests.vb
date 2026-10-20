Option Strict On
Option Explicit On

Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-238 - FilePendingQueue against a real temp folder.
<TestClass>
Public Class FilePendingQueueTests

    Private _folder As String
    Private _queue As FilePendingQueue

    <TestInitialize>
    Public Sub Init()
        _folder = Path.Combine(Path.GetTempPath(), "ErrorLogQueue_" & Guid.NewGuid().ToString("N"))
        _queue = New FilePendingQueue(New DirectoryInfo(_folder))
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        If Directory.Exists(_folder) Then Directory.Delete(_folder, True)
    End Sub

    Private ReadOnly Property PendingFile As String
        Get
            Return Path.Combine(_folder, "ErrorLog_pending.jsonl")
        End Get
    End Property

    Private ReadOnly Property RejectedFile As String
        Get
            Return Path.Combine(_folder, "ErrorLog_pending_rejected.txt")
        End Get
    End Property

    Private Shared Function FullEntry(localRef As String) As ErrorLogEntry
        Return New ErrorLogEntry With {
            .App = "UPLUS", .AppVersion = "9.8.7", .Usr = "user", .Machine = "PC1",
            .Context = "Form.Load", .Reason = "hint ""quoted"" " & vbCrLf & "line two",
            .Message = "boom", .ExceptionType = "System.InvalidOperationException",
            .Trace = "at X" & vbCrLf & "at Y", .SqlNumber = 547, .SqlSeverity = 16, .SqlState = 1,
            .SqlProcedure = "dbo.p_x", .SqlLine = 12, .SqlServer = "SRV",
            .LocalRef = localRef, .Created = New DateTime(2026, 10, 20, 14, 35, 12),
            .TargetServer = "SLUCHDB02", .TargetDatabase = "UPLUS2"}
    End Function

    <TestMethod>
    Public Sub AppendThenReadAll_RoundTripsAllFields()
        Dim original As ErrorLogEntry = FullEntry("F20261020143512A7C3")
        _queue.Append(original)
        _queue.Append(New ErrorLogEntry With {.App = "UPLUS", .Message = "second", .LocalRef = "F20261020143513B8D4"})

        Dim lines As PendingLine() = _queue.ReadAll()

        Assert.AreEqual(2, lines.Length)
        Assert.AreEqual(2, File.ReadAllLines(PendingFile).Length, "one line per entry")
        Dim e As ErrorLogEntry = lines(0).Entry
        Assert.IsNotNull(e)
        Assert.IsFalse(String.IsNullOrEmpty(lines(0).Raw))
        Assert.AreEqual(original.App, e.App)
        Assert.AreEqual(original.AppVersion, e.AppVersion)
        Assert.AreEqual(original.Usr, e.Usr)
        Assert.AreEqual(original.Machine, e.Machine)
        Assert.AreEqual(original.Context, e.Context)
        Assert.AreEqual(original.Reason, e.Reason)
        Assert.AreEqual(original.Message, e.Message)
        Assert.AreEqual(original.ExceptionType, e.ExceptionType)
        Assert.AreEqual(original.Trace, e.Trace)
        Assert.AreEqual(original.SqlNumber, e.SqlNumber)
        Assert.AreEqual(original.SqlSeverity, e.SqlSeverity)
        Assert.AreEqual(original.SqlState, e.SqlState)
        Assert.AreEqual(original.SqlProcedure, e.SqlProcedure)
        Assert.AreEqual(original.SqlLine, e.SqlLine)
        Assert.AreEqual(original.SqlServer, e.SqlServer)
        Assert.AreEqual(original.LocalRef, e.LocalRef)
        Assert.AreEqual(original.Created, e.Created)
        Assert.AreEqual(original.TargetServer, e.TargetServer)
        Assert.AreEqual(original.TargetDatabase, e.TargetDatabase)

        Dim second As ErrorLogEntry = lines(1).Entry
        Assert.AreEqual("second", second.Message)
        Assert.IsNull(second.SqlNumber)
        Assert.IsFalse(second.Created.HasValue)
    End Sub

    <TestMethod>
    Public Sub ReadAll_NoFile_ReturnsEmpty()
        Assert.AreEqual(0, _queue.ReadAll().Length)
    End Sub

    <TestMethod>
    Public Sub ReadAll_CorruptLine_ReturnsLineWithNothingEntry()
        _queue.Append(FullEntry("F20261020143512A7C3"))
        File.AppendAllText(PendingFile, "{not json" & Environment.NewLine)
        _queue.Append(FullEntry("F20261020143513B8D4"))

        Dim lines As PendingLine() = _queue.ReadAll()

        Assert.AreEqual(3, lines.Length)
        Assert.AreEqual("F20261020143512A7C3", lines(0).Entry.LocalRef)
        Assert.IsNull(lines(1).Entry)
        Assert.AreEqual("{not json", lines(1).Raw)
        Assert.AreEqual("F20261020143513B8D4", lines(2).Entry.LocalRef)
    End Sub

    <TestMethod>
    Public Sub Remove_DropsProcessedAndRejected_KeepsLinesAppendedAfterReadAll()
        _queue.Append(FullEntry("F20261020143512AAAA"))
        _queue.Append(FullEntry("F20261020143512BBBB"))
        _queue.Append(FullEntry("F20261020143512CCCC"))
        Dim lines As PendingLine() = _queue.ReadAll()
        _queue.Append(FullEntry("F20261020143512DDDD"))

        _queue.Remove({lines(0)}, {lines(1)})

        Dim remaining As PendingLine() = _queue.ReadAll()
        Assert.AreEqual(2, remaining.Length)
        Assert.AreEqual("F20261020143512CCCC", remaining(0).Entry.LocalRef)
        Assert.AreEqual("F20261020143512DDDD", remaining(1).Entry.LocalRef, "a line appended after ReadAll is kept")
        Dim rejected As String() = File.ReadAllLines(RejectedFile)
        Assert.AreEqual(1, rejected.Length)
        StringAssert.Contains(rejected(0), lines(1).Raw)
        StringAssert.Matches(rejected(0), New Text.RegularExpressions.Regex("^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} "))
    End Sub

    <TestMethod>
    Public Sub Remove_DuplicateRaw_RemovesOneOccurrencePerMatch()
        Dim entry As ErrorLogEntry = FullEntry("F20261020143512AAAA")
        _queue.Append(entry)
        _queue.Append(entry)

        _queue.Remove({_queue.ReadAll()(0)}, New PendingLine() {})

        Assert.AreEqual(1, _queue.ReadAll().Length)
        Assert.IsFalse(File.Exists(RejectedFile), "no rejected lines, no rejected file")
    End Sub

    <TestMethod>
    Public Sub Append_WhileFileHeldExclusively_ThrowsAfterRetries()
        Directory.CreateDirectory(_folder)
        Dim watch As Stopwatch = Nothing
        Using held As New FileStream(PendingFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            watch = Stopwatch.StartNew()
            Dim caught As Exception = Nothing
            Try
                _queue.Append(FullEntry("F20261020143512A7C3"))
            Catch ex As Exception
                caught = ex
            End Try
            watch.Stop()
            Assert.IsInstanceOfType(caught, GetType(IOException))
        End Using
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString())
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromMilliseconds(300), "retried before giving up: " & watch.Elapsed.ToString())
    End Sub

End Class
