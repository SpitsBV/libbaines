Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports System.Threading.Tasks
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-227 - Report(..., hint) and ReportText(...). Text rule with a non-empty hint:
' hint & vbCrLf & vbCrLf & <text without hint>.
<TestClass>
Public Class ErrorLogHintTests

    Private Const ConnectionString As String = "Data Source=.;Initial Catalog=x;Integrated Security=True"
    Private Const Hint As String = "A problem occurred in file with id 5."

    Private Enum Row
        NotConfiguredSql
        NotConfiguredPlain
        ConfiguredPlain
        ConfiguredSqlWriteOk
        WriterThrowsConnection53
        WriterThrowsOther547
    End Enum

    Private Shared ReadOnly AllRows As Row() = DirectCast([Enum].GetValues(GetType(Row)), Row())

    ''' <summary>Fresh fakes wired into ErrorLog, configured per row.</summary>
    Private NotInheritable Class Scenario
        Friend ReadOnly Writer As New FakeWriter()
        Friend ReadOnly Presenter As New FakePresenter()
        Friend ReadOnly Fallback As New FakeFallback()
        Friend ReadOnly Row As Row
        Friend ReadOnly Ex As Exception
        ''' <summary>Text shown without a hint.</summary>
        Friend ReadOnly BaseText As String
        Friend ReadOnly ExpectedReturn As Integer?
        Friend ReadOnly ExpectsFallback As Boolean
        Friend ReadOnly ExpectsWrite As Boolean

        Friend Sub New(r As Row, Optional exOverride As Exception = Nothing)
            Row = r
            ErrorLog.Reset()
            ErrorLog.SetSeams(Writer, Presenter, Fallback)
            Writer.ResultId = 42
            Writer.ResultText = "T"
            Select Case r
                Case Row.NotConfiguredSql
                    Ex = If(exOverride, SqlExceptionFactory.Create(547, message:="not configured sql message"))
                    BaseText = Ex.Message
                Case Row.NotConfiguredPlain
                    Ex = If(exOverride, New InvalidOperationException("not configured plain message"))
                    BaseText = Ex.Message
                Case Row.ConfiguredPlain
                    ErrorLog.Configure(ConnectionString, "UPLUS", "1.2.3")
                    Ex = If(exOverride, New InvalidOperationException("configured plain message"))
                    BaseText = Ex.Message
                Case Row.ConfiguredSqlWriteOk
                    ErrorLog.Configure(ConnectionString, "UPLUS", "1.2.3")
                    Ex = If(exOverride, SqlExceptionFactory.Create(547, message:="fk message"))
                    BaseText = "T" & vbCrLf & vbCrLf & "Foutcode: 42. Geef deze code door aan de beheerder."
                    ExpectedReturn = 42
                    ExpectsWrite = True
                Case Row.WriterThrowsConnection53
                    ErrorLog.Configure(ConnectionString, "UPLUS", "1.2.3")
                    Writer.ThrowOnWrite = New InvalidOperationException("writer exploded")
                    Ex = If(exOverride, SqlExceptionFactory.Create(53, message:="network path"))
                    BaseText = ErrorLog.ConnectionFailedText
                    ExpectsWrite = True
                    ExpectsFallback = True
                Case Row.WriterThrowsOther547
                    ErrorLog.Configure(ConnectionString, "UPLUS", "1.2.3")
                    Writer.ThrowOnWrite = New InvalidOperationException("writer exploded")
                    Ex = If(exOverride, SqlExceptionFactory.Create(547, message:="fk message"))
                    BaseText = ErrorLog.LogFailedText
                    ExpectsWrite = True
                    ExpectsFallback = True
            End Select
        End Sub

        Friend Function Expected(hint As String) As String
            If String.IsNullOrEmpty(hint) Then Return BaseText
            Return hint & vbCrLf & vbCrLf & BaseText
        End Function
    End Class

    <TestCleanup>
    Public Sub Cleanup()
        ErrorLog.Reset()
    End Sub

    Private Shared Sub AssertEntriesEqual(a As ErrorLogEntry, b As ErrorLogEntry, label As String)
        Assert.AreEqual(a.App, b.App, label & " App")
        Assert.AreEqual(a.AppVersion, b.AppVersion, label & " AppVersion")
        Assert.AreEqual(a.Usr, b.Usr, label & " Usr")
        Assert.AreEqual(a.Machine, b.Machine, label & " Machine")
        Assert.AreEqual(a.Context, b.Context, label & " Context")
        Assert.AreEqual(a.Message, b.Message, label & " Message")
        Assert.AreEqual(a.ExceptionType, b.ExceptionType, label & " ExceptionType")
        Assert.AreEqual(a.Trace, b.Trace, label & " Trace")
        Assert.AreEqual(a.SqlNumber, b.SqlNumber, label & " SqlNumber")
        Assert.AreEqual(a.SqlSeverity, b.SqlSeverity, label & " SqlSeverity")
        Assert.AreEqual(a.SqlState, b.SqlState, label & " SqlState")
        Assert.AreEqual(a.SqlProcedure, b.SqlProcedure, label & " SqlProcedure")
        Assert.AreEqual(a.SqlLine, b.SqlLine, label & " SqlLine")
        Assert.AreEqual(a.SqlServer, b.SqlServer, label & " SqlServer")
    End Sub

    ' ------------------------------------------------------------------
    ' 1. Report with hint, each row
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Report_WithHint_ShowsHintParagraphThenBaseText_EveryRow()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            Dim result As Integer? = ErrorLog.Report(s.Ex, "ctx", hint:=Hint)

            Assert.AreEqual(1, s.Presenter.CallCount, r.ToString())
            Assert.AreEqual(Hint & vbCrLf & vbCrLf & s.BaseText, s.Presenter.Shown(0), r.ToString())
            Assert.AreEqual(s.ExpectedReturn, result, r.ToString() & " return value")
        Next
    End Sub

    <TestMethod>
    Public Sub Report_WithHint_ConfiguredSqlOk_ExactText()
        Dim s As New Scenario(Row.ConfiguredSqlWriteOk)

        Dim result As Integer? = ErrorLog.Report(s.Ex, "ctx", hint:="Hint tekst")

        Assert.AreEqual(42, result.Value)
        Assert.AreEqual("Hint tekst" & vbCrLf & vbCrLf & "T" & vbCrLf & vbCrLf &
                        "Foutcode: 42. Geef deze code door aan de beheerder.", s.Presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub Report_WithHint_NotConfigured_ExactText()
        Dim s As New Scenario(Row.NotConfiguredPlain, New InvalidOperationException("boom"))

        ErrorLog.Report(s.Ex, "ctx", hint:="Hint tekst")

        Assert.AreEqual("Hint tekst" & vbCrLf & vbCrLf & "boom", s.Presenter.Shown(0))
    End Sub

    <TestMethod>
    Public Sub Report_WithHint_WriterEntryIdenticalToNoHintCall_EveryLoggingRow()
        For Each r As Row In AllRows
            Dim ex As Exception = Nothing
            Dim plain As New Scenario(r)
            ex = plain.Ex
            ErrorLog.Report(ex, "ctx")
            Dim withHint As New Scenario(r, ex)
            ErrorLog.Report(ex, "ctx", hint:=Hint)

            Assert.AreEqual(plain.Writer.CallCount, withHint.Writer.CallCount, r.ToString() & " write count")
            Assert.AreEqual(plain.Fallback.CallCount, withHint.Fallback.CallCount, r.ToString() & " fallback count")
            Assert.AreEqual(If(plain.ExpectsWrite, 1, 0), withHint.Writer.CallCount, r.ToString())
            For i As Integer = 0 To plain.Writer.CallCount - 1
                AssertEntriesEqual(plain.Writer.Entries(i), withHint.Writer.Entries(i), r.ToString())
            Next
        Next
    End Sub

    <TestMethod>
    Public Sub Report_WithHint_HintIsNeverLogged()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            ErrorLog.Report(s.Ex, "ctx", hint:=Hint)

            For Each e As ErrorLogEntry In s.Writer.Entries
                Assert.AreEqual("ctx", e.Context, r.ToString())
                Assert.IsFalse(e.Message.Contains(Hint), r.ToString() & " Message")
                Assert.IsFalse(If(e.Trace, "").Contains(Hint), r.ToString() & " Trace")
            Next
            For Each t As String In s.Fallback.Written
                Assert.IsFalse(t.Contains(Hint), r.ToString() & " fallback")
            Next
        Next
    End Sub

    ' ------------------------------------------------------------------
    ' 2. showMessage:=False with hint
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Report_WithHint_ShowMessageFalse_PresenterNeverCalled_EveryRow()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            Dim result As Integer? = ErrorLog.Report(s.Ex, "ctx", showMessage:=False, hint:=Hint)

            Assert.AreEqual(0, s.Presenter.CallCount, r.ToString())
            Assert.AreEqual(s.ExpectedReturn, result, r.ToString())
            Assert.AreEqual(If(s.ExpectsWrite, 1, 0), s.Writer.CallCount, r.ToString())
            Assert.AreEqual(If(s.ExpectsFallback, 1, 0), s.Fallback.CallCount, r.ToString())
        Next
    End Sub

    ' ------------------------------------------------------------------
    ' 3. Empty hint behaves as Nothing
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub Report_EmptyHint_BehavesAsNothing_EveryRow()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            Dim result As Integer? = ErrorLog.Report(s.Ex, "ctx", hint:="")

            Assert.AreEqual(1, s.Presenter.CallCount, r.ToString())
            Assert.AreEqual(s.BaseText, s.Presenter.Shown(0), r.ToString())
            Assert.AreEqual(s.ExpectedReturn, result, r.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub Report_NothingHint_ExplicitlyPassed_BehavesAsNoHint_EveryRow()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            ErrorLog.Report(s.Ex, "ctx", hint:=Nothing)

            Assert.AreEqual(s.BaseText, s.Presenter.Shown(0), r.ToString())
        Next
    End Sub

    ' ------------------------------------------------------------------
    ' 4. ReportText
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub ReportText_ReturnsExactlyWhatReportShows_WithAndWithoutHint_EveryRow()
        For Each r As Row In AllRows
            For Each h As String In New String() {Nothing, "", Hint}
                Dim viaReport As New Scenario(r)
                ErrorLog.Report(viaReport.Ex, "ctx", hint:=h)
                Dim viaText As New Scenario(r, viaReport.Ex)

                Dim text As String = ErrorLog.ReportText(viaText.Ex, "ctx", h)

                Dim label As String = r.ToString() & " hint=" & If(h, "<Nothing>")
                Assert.AreEqual(viaReport.Presenter.Shown(0), text, label)
                Assert.AreEqual(viaText.Expected(h), text, label)
            Next
        Next
    End Sub

    <TestMethod>
    Public Sub ReportText_NeverCallsPresenter_AndLogsSameAsReport_EveryRow()
        For Each r As Row In AllRows
            Dim viaReport As New Scenario(r)
            ErrorLog.Report(viaReport.Ex, "ctx", hint:=Hint)
            Dim viaText As New Scenario(r, viaReport.Ex)

            ErrorLog.ReportText(viaText.Ex, "ctx", Hint)

            Assert.AreEqual(0, viaText.Presenter.CallCount, r.ToString() & " presenter")
            Assert.AreEqual(viaReport.Writer.CallCount, viaText.Writer.CallCount, r.ToString() & " writes")
            Assert.AreEqual(viaReport.Fallback.CallCount, viaText.Fallback.CallCount, r.ToString() & " fallback")
            Assert.AreEqual(If(viaText.ExpectsFallback, 1, 0), viaText.Fallback.CallCount, r.ToString())
            For i As Integer = 0 To viaReport.Writer.CallCount - 1
                AssertEntriesEqual(viaReport.Writer.Entries(i), viaText.Writer.Entries(i), r.ToString())
            Next
        Next
    End Sub

    <TestMethod>
    Public Sub ReportText_WithoutHintArgument_OmittedOptional_ReturnsBaseText_EveryRow()
        For Each r As Row In AllRows
            Dim s As New Scenario(r)

            Dim text As String = ErrorLog.ReportText(s.Ex, "ctx")

            Assert.AreEqual(s.BaseText, text, r.ToString())
            Assert.AreEqual(0, s.Presenter.CallCount, r.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub ReportText_ConfiguredSqlOk_ExactText()
        Dim s As New Scenario(Row.ConfiguredSqlWriteOk)

        Dim text As String = ErrorLog.ReportText(s.Ex, "ctx", "Hint tekst")

        Assert.AreEqual("Hint tekst" & vbCrLf & vbCrLf & "T" & vbCrLf & vbCrLf &
                        "Foutcode: 42. Geef deze code door aan de beheerder.", text)
        Assert.AreEqual(1, s.Writer.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 5. ReportText never throws
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub ReportText_WriterFallbackAndPresenterAllThrow_ReturnsFailureTextWithoutThrowing()
        For Each number As Integer In New Integer() {53, 547}
            For Each h As String In New String() {Nothing, Hint}
                Dim s As New Scenario(Row.ConfiguredSqlWriteOk, SqlExceptionFactory.Create(number))
                s.Writer.ThrowOnWrite = New InvalidOperationException("writer boom")
                s.Presenter.ThrowOnShow = New InvalidOperationException("presenter boom")
                s.Fallback.ThrowOnWrite = New InvalidOperationException("fallback boom")
                Dim failure As String = If(number = 53, ErrorLog.ConnectionFailedText, ErrorLog.LogFailedText)
                Dim expected As String = If(String.IsNullOrEmpty(h), failure, h & vbCrLf & vbCrLf & failure)

                Dim text As String = Nothing
                Try
                    text = ErrorLog.ReportText(s.Ex, "ctx", h)
                Catch ex As Exception
                    Assert.Fail("ReportText threw: " & ex.ToString())
                End Try

                Assert.IsFalse(String.IsNullOrEmpty(text), "number " & number.ToString())
                Assert.AreEqual(expected, text, "number " & number.ToString())
            Next
        Next
    End Sub

    <TestMethod>
    Public Sub ReportText_NothingException_DoesNotThrow()
        Dim s As New Scenario(Row.ConfiguredPlain)
        Try
            ErrorLog.ReportText(Nothing, "ctx")
            ErrorLog.ReportText(Nothing, "ctx", Hint)
            ErrorLog.ReportText(s.Ex, Nothing, Hint)
        Catch ex As Exception
            Assert.Fail("ReportText threw: " & ex.ToString())
        End Try
    End Sub

    <TestMethod>
    Public Sub ReportText_NotConfigured_PresenterThrows_DoesNotThrowAndPresenterUntouched()
        Dim s As New Scenario(Row.NotConfiguredPlain)
        s.Presenter.ThrowOnShow = New InvalidOperationException("presenter boom")

        Dim text As String = ErrorLog.ReportText(s.Ex, "ctx", Hint)

        Assert.AreEqual(Hint & vbCrLf & vbCrLf & s.Ex.Message, text)
        Assert.AreEqual(0, s.Presenter.CallCount)
    End Sub

    ' ------------------------------------------------------------------
    ' 6. ReportText re-entrancy and thread safety
    ' ------------------------------------------------------------------

    <TestMethod>
    Public Sub ReportText_Reentrancy_InnerReportTextFromWriter_GoesToFallback_OuterCompletes()
        Dim s As New Scenario(Row.ConfiguredSqlWriteOk)
        Dim innerCalled As Boolean = False
        s.Writer.OnWrite = Sub(entry As ErrorLogEntry)
                               If innerCalled Then Return
                               innerCalled = True
                               ErrorLog.ReportText(SqlExceptionFactory.Create(53, message:="writer's own failure"), "inner")
                           End Sub

        Dim text As String = ErrorLog.ReportText(s.Ex, "outer", Hint)

        Assert.IsTrue(innerCalled)
        Assert.AreEqual(1, s.Writer.CallCount, "inner ReportText must not reach the writer")
        Assert.AreEqual("outer", s.Writer.Entries(0).Context)
        Assert.AreEqual(1, s.Fallback.CallCount)
        StringAssert.Contains(s.Fallback.Written(0), "inner")
        StringAssert.Contains(s.Fallback.Written(0), "writer's own failure")
        Assert.AreEqual(Hint & vbCrLf & vbCrLf & "T" & vbCrLf & vbCrLf &
                        "Foutcode: 42. Geef deze code door aan de beheerder.", text)
        Assert.AreEqual(0, s.Presenter.CallCount)
    End Sub

    <TestMethod>
    Public Sub ReportText_Reentrancy_InnerReportFromWriter_AlsoGuarded()
        Dim s As New Scenario(Row.ConfiguredSqlWriteOk)
        Dim innerCalled As Boolean = False
        s.Writer.OnWrite = Sub(entry As ErrorLogEntry)
                               If innerCalled Then Return
                               innerCalled = True
                               ErrorLog.Report(SqlExceptionFactory.Create(53), "inner", showMessage:=False, hint:=Hint)
                           End Sub

        ErrorLog.ReportText(s.Ex, "outer")

        Assert.AreEqual(1, s.Writer.CallCount)
        Assert.AreEqual(1, s.Fallback.CallCount)
    End Sub

    <TestMethod>
    Public Sub ReportText_TwentyParallelCalls_TwentyWrites_NoExceptions_NoPresenterCalls()
        Dim s As New Scenario(Row.ConfiguredSqlWriteOk)
        s.Writer.Sequential = True
        Dim tasks As New List(Of Task(Of String))
        For i As Integer = 1 To 20
            Dim n As Integer = i
            tasks.Add(Task.Run(Function() ErrorLog.ReportText(SqlExceptionFactory.Create(547), "ctx" & n.ToString(), Hint)))
        Next

        Try
            Task.WaitAll(tasks.ToArray())
        Catch ex As Exception
            Assert.Fail("A parallel ReportText threw: " & ex.ToString())
        End Try

        Assert.AreEqual(20, s.Writer.CallCount)
        Assert.AreEqual(0, s.Presenter.CallCount)
        Assert.AreEqual(0, s.Fallback.CallCount)
        Dim texts As New HashSet(Of String)
        For Each t As Task(Of String) In tasks
            Assert.IsTrue(t.Result.StartsWith(Hint & vbCrLf & vbCrLf))
            Assert.IsTrue(t.Result.Contains("Foutcode: "))
            texts.Add(t.Result)
        Next
        Assert.AreEqual(20, texts.Count, "each call returns the foutcode of its own write")
    End Sub

End Class
