Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports System.Threading
Imports System.Windows.Forms

''' <summary>
''' CH-227: central error reporting. Not configured (spits): behaves exactly as the
''' MsgBox(ex.Message) it replaces. Configured (UPLUS): SQL errors are logged through
''' dbo.p_insert_error_log and the user gets a generic text with a foutcode.
''' Non-SQL exceptions keep MsgBox(ex.Message) until CH-238 extends logging to them.
''' </summary>
Public NotInheritable Class ErrorLog

    Private Const DefaultConnectionFailedText As String =
        "Er kon geen verbinding met de database worden gemaakt. Probeer het later opnieuw of neem contact op met de beheerder."
    Private Const DefaultLogFailedText As String =
        "Er is een fout opgetreden. De fout kon niet worden vastgelegd; neem contact op met de beheerder."

    ' SQL error numbers that mean the database could not be reached. Mirrors the 9002 rows in
    ' b_error_sql_number: when the database is down there is no mapping to read.
    Private Shared ReadOnly ConnectionErrorNumbers As Integer() =
        {-1, 2, 53, 233, 4060, 10053, 10054, 10060, 18456, 40613}

    Private Shared ReadOnly _sync As New Object()
    Private Shared _enabled As Boolean
    Private Shared _appName As String
    Private Shared _appVersion As String
    Private Shared _writer As IErrorLogWriter
    Private Shared _presenter As IErrorPresenter = New MessageBoxPresenter(Nothing)
    Private Shared _fallback As IFallbackLog = New FileFallbackLog(Nothing)
    Private Shared _seamsOverridden As Boolean

    <ThreadStatic> Private Shared _reporting As Boolean

    Private Sub New()
    End Sub

    Public Shared Property ConnectionFailedText As String = DefaultConnectionFailedText
    Public Shared Property LogFailedText As String = DefaultLogFailedText

    Public Shared ReadOnly Property IsEnabled As Boolean
        Get
            Return _enabled
        End Get
    End Property

    ''' <summary>Enables logging. Call on the UI thread once the connection string is known.</summary>
    Public Shared Sub Configure(connectionString As String, appName As String, appVersion As String)
        If String.IsNullOrEmpty(connectionString) Then
            Throw New ArgumentException("A connection string is required.", NameOf(connectionString))
        End If
        SyncLock _sync
            _appName = appName
            _appVersion = appVersion
            If Not _seamsOverridden Then
                _writer = New SqlErrorLogWriter(connectionString)
                _fallback = New FileFallbackLog(appName)
                If TypeOf SynchronizationContext.Current Is WindowsFormsSynchronizationContext Then
                    _presenter = New MessageBoxPresenter(SynchronizationContext.Current)
                End If
            End If
            _enabled = True
        End SyncLock
    End Sub

    ''' <summary>
    ''' Handles an exception caught by the caller. Returns the foutcode when a log row was written.
    ''' Never throws.
    ''' </summary>
    Public Shared Function Report(ex As Exception, context As String,
                                  Optional showMessage As Boolean = True) As Integer?
        If ex Is Nothing Then Return Nothing
        Try
            Dim sqlEx As SqlException = If(_enabled, FindSqlException(ex), Nothing)
            If sqlEx Is Nothing Then
                If showMessage Then _presenter.Show(ex.Message)
                Return Nothing
            End If

            Dim entry As ErrorLogEntry = BuildEntry(ex, sqlEx, context)
            If _reporting Then
                ' Reporting failed while reporting: do not recurse.
                _fallback.Write(entry.ToString())
                Return Nothing
            End If

            _reporting = True
            Try
                Dim result As ErrorLogResult = _writer.Write(entry)
                If showMessage Then
                    _presenter.Show(result.UserMessage & vbCrLf & vbCrLf &
                                    "Foutcode: " & result.ErrorLogId.ToString() &
                                    ". Geef deze code door aan de beheerder.")
                End If
                Return result.ErrorLogId
            Catch writeEx As Exception
                Try
                    _fallback.Write(entry.ToString() & Environment.NewLine &
                                    "Logging to the database failed: " & writeEx.ToString())
                Catch fallbackEx As Exception
                    ' The fallback file is unavailable too; the user must still be informed below.
                    Debug.WriteLine("ErrorLog: fallback failed: " & fallbackEx.ToString())
                End Try
                If showMessage Then
                    _presenter.Show(If(IsConnectionError(sqlEx), ConnectionFailedText, LogFailedText))
                End If
                Return Nothing
            Finally
                _reporting = False
            End Try
        Catch reportEx As Exception
            WriteLastResort(ex, context, reportEx)
            Return Nothing
        End Try
    End Function

    ''' <summary>First SqlException in ex, its InnerException chain or AggregateException members.</summary>
    Public Shared Function FindSqlException(ex As Exception) As SqlException
        If ex Is Nothing Then Return Nothing
        Dim sqlEx As SqlException = TryCast(ex, SqlException)
        If sqlEx IsNot Nothing Then Return sqlEx
        Dim aggregate As AggregateException = TryCast(ex, AggregateException)
        If aggregate IsNot Nothing Then
            For Each inner As Exception In aggregate.InnerExceptions
                Dim found As SqlException = FindSqlException(inner)
                If found IsNot Nothing Then Return found
            Next
        End If
        Return FindSqlException(ex.InnerException)
    End Function

    Friend Shared Sub SetSeams(writer As IErrorLogWriter, presenter As IErrorPresenter, fallback As IFallbackLog)
        SyncLock _sync
            _writer = writer
            _presenter = presenter
            _fallback = fallback
            _seamsOverridden = True
        End SyncLock
    End Sub

    Friend Shared Sub Reset()
        SyncLock _sync
            _enabled = False
            _appName = Nothing
            _appVersion = Nothing
            _writer = Nothing
            _presenter = New MessageBoxPresenter(Nothing)
            _fallback = New FileFallbackLog(Nothing)
            _seamsOverridden = False
            ConnectionFailedText = DefaultConnectionFailedText
            LogFailedText = DefaultLogFailedText
        End SyncLock
    End Sub

    Private Shared Function BuildEntry(ex As Exception, sqlEx As SqlException, context As String) As ErrorLogEntry
        Return New ErrorLogEntry With {
            .App = _appName,
            .AppVersion = _appVersion,
            .Usr = Environment.UserName,
            .Machine = Environment.MachineName,
            .Context = If(context, ""),
            .Message = ex.Message,
            .ExceptionType = ex.GetType().FullName,
            .Trace = ex.ToString(),
            .SqlNumber = sqlEx.Number,
            .SqlSeverity = sqlEx.Class,
            .SqlState = sqlEx.State,
            .SqlProcedure = If(sqlEx.Procedure, ""),
            .SqlLine = sqlEx.LineNumber,
            .SqlServer = If(sqlEx.Server, "")
        }
    End Function

    Private Shared Function IsConnectionError(sqlEx As SqlException) As Boolean
        Return Array.IndexOf(ConnectionErrorNumbers, sqlEx.Number) >= 0
    End Function

    Private Shared Sub WriteLastResort(ex As Exception, context As String, reportEx As Exception)
        Try
            _fallback.Write(String.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}{3}Reporting failed: {4}",
                                          DateTime.Now, context, ex, Environment.NewLine, reportEx))
        Catch fallbackEx As Exception
            ' Nothing is left to report to: the database and the file log both failed. Report must
            ' never throw (it runs inside catch blocks and the unhandled-exception handler), so this
            ' is the one place an exception is deliberately dropped.
            Debug.WriteLine("ErrorLog: fallback failed: " & fallbackEx.ToString())
        End Try
    End Sub
End Class
