Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-227 - Utilities.ErrorLog, tested through the Friend seams (IErrorLogWriter, IErrorPresenter, IFallbackLog).
<TestClass>
Public Class ErrorLogTests

    Private Const ConnectionString As String = "Data Source=.;Initial Catalog=x;Integrated Security=True"
    Private Const AppName As String = "UPLUS"
    Private Const AppVersion As String = "9.8.7"

    Private _writer As FakeWriter
    Private _presenter As FakePresenter
    Private _fallback As FakeFallback

    <TestInitialize>
    Public Sub Init()
        ErrorLog.Reset()
        _writer = New FakeWriter()
        _presenter = New FakePresenter()
        _fallback = New FakeFallback()
        ErrorLog.SetSeams(_writer, _presenter, _fallback)
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        ErrorLog.Reset()
    End Sub

    Private Sub ConfigureLogging()
        ErrorLog.Configure(ConnectionString, AppName, AppVersion)
    End Sub

    Private Shared Function Thrown(ex As Exception) As Exception
        ' Throw and catch so the exception has a stack trace, like a real one.
        Try
            Throw ex
        Catch caught As Exception
            Return caught
        End Try
    End Function

    Private Shared Function Wrap(inner As Exception, levels As Integer) As Exception
        Dim current As Exception = inner
        For i As Integer = 1 To levels
            current = New InvalidOperationException("wrapper " & i.ToString(), current)
        Next
        Return current
    End Function

    ' ------------------------------------------------------------------
    ' 1. FindSqlException
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub FindSqlException_SqlExceptionItself_ReturnsSameInstance()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547)
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(sqlEx))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_WrappedOneLevel_Finds()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547)
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(Wrap(sqlEx, 1)))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_WrappedThreeLevels_Finds()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547)
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(Wrap(sqlEx, 3)))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_InAggregateNotFirstElement_Finds()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(2627)
        Dim agg As New AggregateException(New InvalidOperationException("first"), New ArgumentException("second"), sqlEx)
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(agg))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_AggregateWrappedInInner_Finds()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(2627)
        Dim agg As New AggregateException(New InvalidOperationException("first"), sqlEx)
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(Wrap(agg, 2)))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_SqlExceptionInsideInnerOfAggregateElement_Finds()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(229)
        Dim agg As New AggregateException(New InvalidOperationException("first"), Wrap(sqlEx, 1))
        Assert.AreSame(sqlEx, ErrorLog.FindSqlException(agg))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_PlainException_ReturnsNothing()
        Assert.IsNull(ErrorLog.FindSqlException(New InvalidOperationException("plain")))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_PlainExceptionWithInnerChain_ReturnsNothing()
        Assert.IsNull(ErrorLog.FindSqlException(Wrap(New ArgumentException("deep"), 3)))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_AggregateWithoutSql_ReturnsNothing()
        Dim agg As New AggregateException(New InvalidOperationException("a"), New ArgumentException("b"))
        Assert.IsNull(ErrorLog.FindSqlException(agg))
    End Sub

    <TestMethod>
    Public Sub FindSqlException_NothingInput_ReturnsNothing()
        Assert.IsNull(ErrorLog.FindSqlException(Nothing))
    End Sub

    ' ------------------------------------------------------------------
    ' 2. Not configured
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub NotConfigured_IsEnabledFalse()
        Assert.IsFalse(ErrorLog.IsEnabled)
    End Sub

    <TestMethod>
    Public Sub NotConfigured_SqlException_ShowsMessageOnly_NoWrite_ReturnsNothing()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547, message:="FK violation")

        Dim result As Integer? = ErrorLog.Report(sqlEx, "ctx")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual(sqlEx.Message, _presenter.Shown(0))
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    <TestMethod>
    Public Sub NotConfigured_NonSqlException_ShowsMessageOnly_NoWrite_ReturnsNothing()
        Dim ex As New InvalidOperationException("plain failure")

        Dim result As Integer? = ErrorLog.Report(ex, "ctx")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual("plain failure", _presenter.Shown(0))
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    <TestMethod>
    Public Sub NotConfigured_WrappedSqlException_ShowsOuterMessage_NoWrite()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547)
        Dim outer As Exception = Wrap(sqlEx, 2)

        Dim result As Integer? = ErrorLog.Report(outer, "ctx")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual(outer.Message, _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub NotConfigured_ShowMessageFalse_PresenterNeverCalled()
        Dim r1 As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx", showMessage:=False)
        Dim r2 As Integer? = ErrorLog.Report(New InvalidOperationException("x"), "ctx", showMessage:=False)

        Assert.IsFalse(r1.HasValue)
        Assert.IsFalse(r2.HasValue)
        Assert.AreEqual(0, _presenter.CallCount)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 3. Configured, non-SQL exception (the CH-238 gate)
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Configured_NonSqlException_ShowsMessage_NoWrite_ReturnsNothing()
        ConfigureLogging()
        Dim ex As New InvalidOperationException("plain failure")

        Dim result As Integer? = ErrorLog.Report(ex, "ctx")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(0, _fallback.CallCount)
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual("plain failure", _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub Configured_NonSqlException_ShowMessageFalse_NothingHappens()
        ConfigureLogging()

        Dim result As Integer? = ErrorLog.Report(New InvalidOperationException("plain failure"), "ctx", showMessage:=False)

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(0, _writer.CallCount)
        Assert.AreEqual(0, _presenter.CallCount)
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 4. Configured, SQL exception, writer succeeds
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Configured_SqlException_WriterSucceeds_ReturnsErrorCode()
        ConfigureLogging()
        _writer.ResultId = 42
        _writer.ResultText = "T"

        Dim result As Integer? = ErrorLog.Report(Thrown(SqlExceptionFactory.Create(547)), "ctx")

        Assert.IsTrue(result.HasValue)
        Assert.AreEqual(42, result.Value)
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    <TestMethod>
    Public Sub Configured_SqlException_WriterSucceeds_PresenterShowsUserTextAndErrorCode()
        ConfigureLogging()
        _writer.ResultId = 42
        _writer.ResultText = "T"

        ErrorLog.Report(Thrown(SqlExceptionFactory.Create(547)), "ctx")

        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual("T" & vbCrLf & vbCrLf & "Error code: 42. Pass this code on to the administrator.", _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub Configured_SqlException_WriterSucceeds_EntryHasAllFieldsFromDirectSqlException()
        ConfigureLogging()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(
            547, errorClass:=16, state:=3, procedure:="p_save_thing", lineNumber:=27, server:="SRV01", message:="FK conflict")

        ErrorLog.Report(sqlEx, "ctx")

        Assert.AreEqual(1, _writer.CallCount)
        Dim e As ErrorLogEntry = _writer.Entries(0)
        Assert.AreEqual(AppName, e.App)
        Assert.AreEqual(AppVersion, e.AppVersion)
        Assert.AreEqual(Environment.UserName, e.Usr)
        Assert.AreEqual(Environment.MachineName, e.Machine)
        Assert.AreEqual("ctx", e.Context)
        Assert.AreEqual(sqlEx.Message, e.Message)
        Assert.AreEqual(GetType(SqlException).FullName, e.ExceptionType)
        Assert.AreEqual(sqlEx.ToString(), e.Trace)
        Assert.AreEqual(547, e.SqlNumber.Value)
        Assert.AreEqual(CByte(16), e.SqlSeverity.Value)
        Assert.AreEqual(CByte(3), e.SqlState.Value)
        Assert.AreEqual("p_save_thing", e.SqlProcedure)
        Assert.AreEqual(27, e.SqlLine.Value)
        Assert.AreEqual("SRV01", e.SqlServer)
    End Sub

    <TestMethod>
    Public Sub Configured_WrappedSqlException_EntryUsesOuterForExceptionFieldsAndInnerForSqlFields()
        ConfigureLogging()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(
            2627, errorClass:=14, state:=1, procedure:="p_unique", lineNumber:=9, server:="SRV02", message:="Duplicate key")
        Dim outer As Exception = Thrown(Wrap(sqlEx, 2))

        ErrorLog.Report(outer, "Form.Save")

        Assert.AreEqual(1, _writer.CallCount)
        Dim e As ErrorLogEntry = _writer.Entries(0)
        Assert.AreEqual("Form.Save", e.Context)
        Assert.AreEqual(outer.Message, e.Message)
        Assert.AreEqual(outer.GetType().FullName, e.ExceptionType)
        Assert.AreEqual(outer.ToString(), e.Trace)
        Assert.AreEqual(2627, e.SqlNumber.Value)
        Assert.AreEqual(CByte(14), e.SqlSeverity.Value)
        Assert.AreEqual(CByte(1), e.SqlState.Value)
        Assert.AreEqual("p_unique", e.SqlProcedure)
        Assert.AreEqual(9, e.SqlLine.Value)
        Assert.AreEqual("SRV02", e.SqlServer)
    End Sub

    <TestMethod>
    Public Sub Configured_SqlException_NoProcedure_EntryProcedureIsEmptyString()
        ConfigureLogging()

        ErrorLog.Report(SqlExceptionFactory.Create(547, procedure:=Nothing), "ctx")

        Assert.AreEqual(1, _writer.CallCount)
        Assert.AreEqual("", _writer.Entries(0).SqlProcedure)
    End Sub

    <TestMethod>
    Public Sub Configured_SqlExceptionInAggregate_EntryUsesFoundSqlException()
        ConfigureLogging()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(229, message:="denied")
        Dim agg As New AggregateException(New InvalidOperationException("first"), sqlEx)

        Dim result As Integer? = ErrorLog.Report(agg, "ctx")

        Assert.AreEqual(42, result.Value)
        Assert.AreEqual(1, _writer.CallCount)
        Assert.AreEqual(229, _writer.Entries(0).SqlNumber.Value)
        Assert.AreEqual(agg.Message, _writer.Entries(0).Message)
        Assert.AreEqual(GetType(AggregateException).FullName, _writer.Entries(0).ExceptionType)
    End Sub

    <TestMethod>
    Public Sub Configured_SqlException_WriterSucceeds_ShowMessageFalse_LogsButDoesNotShow()
        ConfigureLogging()

        Dim result As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx", showMessage:=False)

        Assert.AreEqual(42, result.Value)
        Assert.AreEqual(1, _writer.CallCount)
        Assert.AreEqual(0, _presenter.CallCount)
        Assert.AreEqual(0, _fallback.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 5. Writer throws, connection error (number 53)
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub WriterThrows_ConnectionError_WritesFallbackOnce_WithContextMessageAndWriterException()
        ConfigureLogging()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(53, message:="Network path not found")
        Dim writerEx As New InvalidOperationException("writer exploded")
        _writer.ThrowOnWrite = writerEx

        Dim result As Integer? = ErrorLog.Report(sqlEx, "Form.Load")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(1, _fallback.CallCount)
        Dim text As String = _fallback.Written(0)
        StringAssert.Contains(text, "Form.Load")
        StringAssert.Contains(text, sqlEx.Message)
        StringAssert.Contains(text, "writer exploded")
    End Sub

    <TestMethod>
    Public Sub WriterThrows_ConnectionError_PresenterShowsConnectionFailedText_NoErrorCode()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        ErrorLog.Report(SqlExceptionFactory.Create(53), "ctx")

        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual(ErrorLog.ConnectionFailedText, _presenter.Shown(0))
        Assert.IsFalse(_presenter.Shown(0).Contains("Error code"))
    End Sub

    <TestMethod>
    Public Sub WriterThrows_ConnectionError_WrappedSql_StillConnectionText()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        ErrorLog.Report(Wrap(SqlExceptionFactory.Create(53), 2), "ctx")

        Assert.AreEqual(ErrorLog.ConnectionFailedText, _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub WriterThrows_AllConnectionNumbers_ShowConnectionFailedText()
        For Each number As Integer In New Integer() {-1, 2, 53, 233, 4060, 10053, 10054, 10060, 18456, 40613}
            Init()
            ConfigureLogging()
            _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

            ErrorLog.Report(SqlExceptionFactory.Create(number), "ctx")

            Assert.AreEqual(1, _presenter.CallCount, "number " & number.ToString())
            Assert.AreEqual(ErrorLog.ConnectionFailedText, _presenter.Shown(0), "number " & number.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub WriterThrows_CustomConnectionFailedText_IsShown()
        ConfigureLogging()
        ErrorLog.ConnectionFailedText = "custom connection text"
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        ErrorLog.Report(SqlExceptionFactory.Create(53), "ctx")

        Assert.AreEqual("custom connection text", _presenter.Shown(0))
    End Sub

    ' ------------------------------------------------------------------
    ' 6. Writer throws, non-connection error (number 547)
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub WriterThrows_NonConnectionError_PresenterShowsLogFailedText_ReturnsNothing()
        ConfigureLogging()
        Dim sqlEx As SqlException = SqlExceptionFactory.Create(547, message:="FK conflict")
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        Dim result As Integer? = ErrorLog.Report(sqlEx, "ctx")

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual(ErrorLog.LogFailedText, _presenter.Shown(0))
        Assert.AreNotEqual(ErrorLog.ConnectionFailedText, _presenter.Shown(0))
        Assert.AreEqual(1, _fallback.CallCount)
        StringAssert.Contains(_fallback.Written(0), "FK conflict")
    End Sub

    <TestMethod>
    Public Sub WriterThrows_CustomLogFailedText_IsShown()
        ConfigureLogging()
        ErrorLog.LogFailedText = "custom log text"
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx")

        Assert.AreEqual("custom log text", _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub WriterSucceeds_CustomErrorCodeText_IsShownWithId()
        ConfigureLogging()
        ErrorLog.ErrorCodeText = "Error code: {0}. Pass this code on."
        _writer.ResultId = 42
        _writer.ResultText = "T"

        ErrorLog.Report(Thrown(SqlExceptionFactory.Create(547)), "ctx")

        Assert.AreEqual("T" & vbCrLf & vbCrLf & "Error code: 42. Pass this code on.", _presenter.Shown(0))
    End Sub

    ' ------------------------------------------------------------------
    ' 7. showMessage:=False (cases 2-6)
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub ShowMessageFalse_WriterThrowsConnection_FallbackWritten_NothingShown()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        Dim result As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(53), "ctx", showMessage:=False)

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(1, _fallback.CallCount)
        Assert.AreEqual(0, _presenter.CallCount)
    End Sub

    <TestMethod>
    Public Sub ShowMessageFalse_WriterThrowsNonConnection_FallbackWritten_NothingShown()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer exploded")

        Dim result As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx", showMessage:=False)

        Assert.IsFalse(result.HasValue)
        Assert.AreEqual(1, _fallback.CallCount)
        Assert.AreEqual(0, _presenter.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 8. Never throws
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Report_WriterPresenterAndFallbackAllThrow_ReturnsNothingWithoutThrowing()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer boom")
        _presenter.ThrowOnShow = New InvalidOperationException("presenter boom")
        _fallback.ThrowOnWrite = New InvalidOperationException("fallback boom")

        Dim result As Integer? = Nothing
        Try
            result = ErrorLog.Report(SqlExceptionFactory.Create(53), "ctx")
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try

        Assert.IsFalse(result.HasValue)
    End Sub

    <TestMethod>
    Public Sub Report_WriterPresenterAndFallbackAllThrow_NonConnectionNumber_DoesNotThrow()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer boom")
        _presenter.ThrowOnShow = New InvalidOperationException("presenter boom")
        _fallback.ThrowOnWrite = New InvalidOperationException("fallback boom")

        Try
            Assert.IsFalse(ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx").HasValue)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
    End Sub

    <TestMethod>
    Public Sub Report_PresenterThrows_WriterSucceeds_DoesNotThrow()
        ConfigureLogging()
        _presenter.ThrowOnShow = New InvalidOperationException("presenter boom")

        Try
            ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx")
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
        Assert.AreEqual(1, _writer.CallCount)
    End Sub

    <TestMethod>
    Public Sub Report_NotConfigured_PresenterThrows_DoesNotThrow()
        _presenter.ThrowOnShow = New InvalidOperationException("presenter boom")

        Try
            Assert.IsFalse(ErrorLog.Report(New InvalidOperationException("x"), "ctx").HasValue)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
    End Sub

    <TestMethod>
    Public Sub Report_ConfiguredNonSql_PresenterThrows_DoesNotThrow()
        ConfigureLogging()
        _presenter.ThrowOnShow = New InvalidOperationException("presenter boom")

        Try
            Assert.IsFalse(ErrorLog.Report(New InvalidOperationException("x"), "ctx").HasValue)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
    End Sub

    <TestMethod>
    Public Sub Report_FallbackThrows_WriterThrows_DoesNotThrow()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer boom")
        _fallback.ThrowOnWrite = New InvalidOperationException("fallback boom")

        Try
            Assert.IsFalse(ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx").HasValue)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
        Assert.AreEqual(1, _presenter.CallCount)
        Assert.AreEqual(ErrorLog.LogFailedText, _presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub Report_NothingException_DoesNotThrow()
        ConfigureLogging()
        Try
            Assert.IsFalse(ErrorLog.Report(Nothing, "ctx").HasValue)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
    End Sub

    <TestMethod>
    Public Sub Report_NothingContext_DoesNotThrow()
        ConfigureLogging()
        Try
            ErrorLog.Report(SqlExceptionFactory.Create(547), Nothing)
        Catch ex As Exception
            Assert.Fail("Report threw: " & ex.ToString())
        End Try
    End Sub

    ' ------------------------------------------------------------------
    ' 9. Re-entrancy
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Reentrancy_WriterCallsReportWithSqlException_InnerGoesToFallback_OuterCompletes()
        ConfigureLogging()
        Dim innerResult As Integer? = 12345
        Dim innerCalled As Boolean = False
        _writer.OnWrite = Sub(entry As ErrorLogEntry)
                              If innerCalled Then Return
                              innerCalled = True
                              innerResult = ErrorLog.Report(SqlExceptionFactory.Create(53, message:="writer's own failure"),
                                                            "inner", showMessage:=False)
                          End Sub

        Dim outerResult As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "outer")

        Assert.IsTrue(innerCalled)
        Assert.IsFalse(innerResult.HasValue, "inner Report must return Nothing")
        Assert.AreEqual(1, _writer.CallCount, "inner Report must not reach the writer again")
        Assert.AreEqual("outer", _writer.Entries(0).Context)
        Assert.AreEqual(1, _fallback.CallCount, "inner Report must write to the fallback")
        StringAssert.Contains(_fallback.Written(0), "inner")
        StringAssert.Contains(_fallback.Written(0), "writer's own failure")
        Assert.IsTrue(outerResult.HasValue)
        Assert.AreEqual(42, outerResult.Value)
        Assert.AreEqual(1, _presenter.CallCount)
        StringAssert.Contains(_presenter.Shown(0), "Error code: 42.")
    End Sub

    <TestMethod>
    Public Sub Reentrancy_GuardIsReleasedAfterReport_NextReportWritesNormally()
        ConfigureLogging()
        _writer.OnWrite = Sub(entry As ErrorLogEntry)
                              If entry.Context = "outer" Then
                                  ErrorLog.Report(SqlExceptionFactory.Create(53), "inner", showMessage:=False)
                              End If
                          End Sub

        ErrorLog.Report(SqlExceptionFactory.Create(547), "outer", showMessage:=False)
        Dim next1 As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "second", showMessage:=False)

        Assert.AreEqual(42, next1.Value)
        Assert.AreEqual(2, _writer.CallCount)
        Assert.AreEqual("second", _writer.Entries(1).Context)
    End Sub

    <TestMethod>
    Public Sub Reentrancy_GuardIsReleasedWhenWriterThrows_NextReportWritesNormally()
        ConfigureLogging()
        _writer.ThrowOnWrite = New InvalidOperationException("writer boom")
        ErrorLog.Report(SqlExceptionFactory.Create(547), "first", showMessage:=False)
        _writer.ThrowOnWrite = Nothing

        Dim result As Integer? = ErrorLog.Report(SqlExceptionFactory.Create(547), "second", showMessage:=False)

        Assert.AreEqual(42, result.Value)
    End Sub

    ' ------------------------------------------------------------------
    ' 10. Thread safety
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub ThreadSafety_TwentyParallelReports_TwentyWrites_NoExceptions()
        ConfigureLogging()
        _writer.Sequential = True
        Dim tasks As New List(Of Task(Of Integer?))
        For i As Integer = 1 To 20
            Dim n As Integer = i
            tasks.Add(Task.Run(Function() ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx" & n.ToString())))
        Next

        Try
            Task.WaitAll(tasks.ToArray())
        Catch ex As Exception
            Assert.Fail("A parallel Report threw: " & ex.ToString())
        End Try

        Assert.AreEqual(20, _writer.CallCount)
        Assert.AreEqual(20, _presenter.CallCount)
        Assert.AreEqual(0, _fallback.CallCount)
        Dim ids As New HashSet(Of Integer)
        For Each t As Task(Of Integer?) In tasks
            Assert.IsTrue(t.Result.HasValue)
            ids.Add(t.Result.Value)
        Next
        Assert.AreEqual(20, ids.Count, "every Report returns the id its own write produced")
        Dim contexts As New HashSet(Of String)
        For Each e As ErrorLogEntry In _writer.Entries
            contexts.Add(e.Context)
        Next
        Assert.AreEqual(20, contexts.Count)
    End Sub

    <TestMethod>
    Public Sub ThreadSafety_ReentrancyGuardIsPerThread_ParallelReportsDoNotBlockEachOther()
        ConfigureLogging()
        ' Every write blocks until all 20 writes are in flight at once: if a guard were global
        ' (not per thread) the other threads would be turned away and the barrier would never fill.
        Dim inFlight As Integer = 0
        Dim allIn As New ManualResetEventSlim(False)
        _writer.OnWrite = Sub(entry As ErrorLogEntry)
                              If Interlocked.Increment(inFlight) = 20 Then allIn.Set()
                              allIn.Wait(TimeSpan.FromSeconds(10))
                          End Sub
        Dim tasks As New List(Of Task(Of Integer?))
        For i As Integer = 1 To 20
            tasks.Add(Task.Factory.StartNew(Function() ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx", showMessage:=False),
                                            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
        Next
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30))

        Assert.AreEqual(20, _writer.CallCount)
        For Each t As Task(Of Integer?) In tasks
            Assert.IsTrue(t.Result.HasValue)
        Next
    End Sub

    ' ------------------------------------------------------------------
    ' 11. Configure
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Configure_SetsIsEnabled()
        Assert.IsFalse(ErrorLog.IsEnabled)
        ConfigureLogging()
        Assert.IsTrue(ErrorLog.IsEnabled)
    End Sub

    Private Shared Sub AssertConfigureThrowsArgumentException(connectionString As String)
        Dim caught As Exception = Nothing
        Try
            ErrorLog.Configure(connectionString, AppName, AppVersion)
        Catch ex As Exception
            caught = ex
        End Try
        Assert.IsNotNull(caught, "Configure must throw")
        Assert.IsInstanceOfType(caught, GetType(ArgumentException))
    End Sub

    <TestMethod>
    Public Sub Configure_NothingConnectionString_ThrowsArgumentException_StaysDisabled()
        AssertConfigureThrowsArgumentException(Nothing)
        Assert.IsFalse(ErrorLog.IsEnabled)
    End Sub

    <TestMethod>
    Public Sub Configure_EmptyConnectionString_ThrowsArgumentException_StaysDisabled()
        AssertConfigureThrowsArgumentException("")
        Assert.IsFalse(ErrorLog.IsEnabled)
    End Sub

    <TestMethod>
    Public Sub Configure_InvalidAfterValid_ThrowsAndLeavesPreviousConfigurationInPlace()
        ConfigureLogging()
        AssertConfigureThrowsArgumentException("")
        Assert.IsTrue(ErrorLog.IsEnabled)
    End Sub

    <TestMethod>
    Public Sub Configure_Twice_LastValuesWin()
        ErrorLog.Configure(ConnectionString, "FIRST", "1.0")
        ErrorLog.Configure(ConnectionString, "SECOND", "2.0")

        Assert.IsTrue(ErrorLog.IsEnabled)
        ErrorLog.Report(SqlExceptionFactory.Create(547), "ctx", showMessage:=False)

        Assert.AreEqual(1, _writer.CallCount)
        Assert.AreEqual("SECOND", _writer.Entries(0).App)
        Assert.AreEqual("2.0", _writer.Entries(0).AppVersion)
    End Sub

    <TestMethod>
    Public Sub Configure_AfterReset_IsDisabledAgain()
        ConfigureLogging()
        ErrorLog.Reset()
        Assert.IsFalse(ErrorLog.IsEnabled)
    End Sub

    ' ------------------------------------------------------------------
    ' 12. Default texts
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub DefaultTexts_AreNonEmptyAndDistinct()
        Assert.IsFalse(String.IsNullOrWhiteSpace(ErrorLog.ConnectionFailedText))
        Assert.IsFalse(String.IsNullOrWhiteSpace(ErrorLog.LogFailedText))
        Assert.AreNotEqual(ErrorLog.ConnectionFailedText, ErrorLog.LogFailedText)
    End Sub

    <TestMethod>
    Public Sub DefaultTexts_AreSettable()
        ErrorLog.ConnectionFailedText = "conn"
        ErrorLog.LogFailedText = "log"
        Assert.AreEqual("conn", ErrorLog.ConnectionFailedText)
        Assert.AreEqual("log", ErrorLog.LogFailedText)
    End Sub

    <TestMethod>
    Public Sub DefaultTexts_RestoredByReset()
        ErrorLog.Reset()
        Dim defaultConnection As String = ErrorLog.ConnectionFailedText
        Dim defaultLog As String = ErrorLog.LogFailedText

        ErrorLog.ConnectionFailedText = "conn"
        ErrorLog.LogFailedText = "log"
        ErrorLog.Reset()

        Assert.AreEqual(defaultConnection, ErrorLog.ConnectionFailedText)
        Assert.AreEqual(defaultLog, ErrorLog.LogFailedText)
    End Sub

End Class
