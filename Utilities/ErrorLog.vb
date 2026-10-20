Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports System.Globalization
Imports System.Threading
Imports System.Windows.Forms

''' <summary>
''' CH-227/CH-238: central error reporting. Not configured (spits): behaves exactly as the
''' MsgBox(ex.Message) it replaces. Configured (UPLUS): every exception, SQL or not, is logged
''' through dbo.p_insert_error_log (the hint as its reason) and the user gets a generic text with
''' an error code. When the log row cannot be written, the entry is queued on this computer under a
''' local code (F + timestamp + 4 hex) that the user gets instead.
''' </summary>
Public NotInheritable Class ErrorLog

    Public Const DefaultConnectionFailedText As String =
        "The database connection could not be established. Try again later or contact the administrator."
    Public Const DefaultLogFailedText As String =
        "An error occurred. The error could not be logged; contact the administrator."
    Public Const DefaultErrorCodeText As String =
        "Error code: {0}. Pass this code on to the administrator."
    Public Const DefaultLocalCodeText As String =
        "Error code: {0} (saved on this computer). Pass this code on to the administrator."

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
    Private Shared _queue As IPendingQueue
    Private Shared _targetServer As String
    Private Shared _targetDatabase As String
    Private Shared _seamsOverridden As Boolean

    <ThreadStatic> Private Shared _reporting As Boolean

    Private Sub New()
    End Sub

    Public Shared Property ConnectionFailedText As String = DefaultConnectionFailedText
    Public Shared Property LogFailedText As String = DefaultLogFailedText
    ' Format string, {0} is the error log id.
    Public Shared Property ErrorCodeText As String = DefaultErrorCodeText
    ' Format string, {0} is the local code of an entry that could not be written to the database.
    Public Shared Property LocalCodeText As String = DefaultLocalCodeText

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
        ' Server and database of a queued entry, so it is only ever replayed into the same database.
        Dim target As New SqlConnectionStringBuilder(connectionString)
        SyncLock _sync
            _appName = appName
            _appVersion = appVersion
            _targetServer = target.DataSource
            _targetDatabase = target.InitialCatalog
            If Not _seamsOverridden Then
                _writer = New SqlErrorLogWriter(connectionString)
                _fallback = New FileFallbackLog(appName)
                _queue = New FilePendingQueue(appName)
                If TypeOf SynchronizationContext.Current Is WindowsFormsSynchronizationContext Then
                    _presenter = New MessageBoxPresenter(SynchronizationContext.Current)
                End If
            End If
            _enabled = True
        End SyncLock
    End Sub

    ''' <summary>
    ''' Handles an exception caught by the caller. Returns the error code when a log row was written.
    ''' A non-empty hint is shown as a first paragraph above the text Report shows and, when logging is
    ''' enabled, stored as the reason of the log row.
    ''' Never throws.
    ''' </summary>
    Public Shared Function Report(ex As Exception, context As String,
                                  Optional showMessage As Boolean = True,
                                  Optional hint As String = Nothing) As Integer?
        Dim logId As Integer? = Nothing
        Dim text As String = Process(ex, context, hint, logId)
        If showMessage AndAlso text IsNot Nothing Then
            Try
                _presenter.Show(text)
            Catch showEx As Exception
                WriteLastResort(ex, context, showEx)
            End Try
        End If
        Return logId
    End Function

    ''' <summary>
    ''' Logs like Report but never shows anything: returns the text Report would have shown (with hint),
    ''' for callers on a worker thread that hand the text to the UI themselves. Nothing when there is
    ''' nothing to show. Never throws.
    ''' </summary>
    Public Shared Function ReportText(ex As Exception, context As String,
                                      Optional hint As String = Nothing) As String
        Dim logId As Integer? = Nothing
        Return Process(ex, context, hint, logId)
    End Function

    ' Shared path of Report and ReportText: logs when enabled, returns the text to show
    ' (Nothing when there is none) and the error code when a row was written. Never throws.
    Private Shared Function Process(ex As Exception, context As String, hint As String,
                                    ByRef logId As Integer?) As String
        logId = Nothing
        If ex Is Nothing Then Return Nothing
        Try
            If Not _enabled Then Return WithHint(hint, ex.Message)

            Dim sqlEx As SqlException = FindSqlException(ex)
            Dim entry As ErrorLogEntry = BuildEntry(ex, sqlEx, context, hint)
            If _reporting Then
                ' Reporting failed while reporting: do not recurse.
                _fallback.Write(entry.ToString())
                Return Nothing
            End If

            Dim text As String
            _reporting = True
            Try
                Dim result As ErrorLogResult = _writer.Write(entry)
                logId = result.ErrorLogId
                text = result.UserMessage & vbCrLf & vbCrLf &
                       String.Format(ErrorCodeText, result.ErrorLogId)
            Catch writeEx As Exception
                Dim localRef As String = NewLocalRef()
                entry.LocalRef = localRef
                entry.Created = DateTime.Now
                Dim queueFailure As String = ""
                Try
                    _queue.Append(entry)
                Catch queueEx As Exception
                    ' Queue file locked or unwritable: the readable fallback below still holds the
                    ' entry and its local code.
                    queueFailure = Environment.NewLine & "Queueing the entry failed: " & queueEx.ToString()
                End Try
                Try
                    _fallback.Write(entry.ToString() & Environment.NewLine &
                                    "Logging to the database failed: " & writeEx.ToString() & queueFailure)
                Catch fallbackEx As Exception
                    ' The fallback file is unavailable too; the user must still be informed.
                    Debug.WriteLine("ErrorLog: fallback failed: " & fallbackEx.ToString())
                End Try
                text = If(sqlEx IsNot Nothing AndAlso IsConnectionError(sqlEx), ConnectionFailedText, LogFailedText) &
                       vbCrLf & vbCrLf & String.Format(LocalCodeText, localRef)
            Finally
                _reporting = False
            End Try
            Return WithHint(hint, text)
        Catch reportEx As Exception
            logId = Nothing
            WriteLastResort(ex, context, reportEx)
            Return Nothing
        End Try
    End Function

    Private Shared Function WithHint(hint As String, text As String) As String
        If String.IsNullOrEmpty(hint) Then Return text
        Return hint & vbCrLf & vbCrLf & text
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

    ''' <summary>Local error code: "F" + yyyyMMddHHmmss + 4 upper-case hex characters, 19 in total.</summary>
    Friend Shared Function NewLocalRef() As String
        Return "F" & DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) &
               Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant()
    End Function

    Friend Shared Sub SetSeams(writer As IErrorLogWriter, presenter As IErrorPresenter, fallback As IFallbackLog,
                               queue As IPendingQueue)
        SyncLock _sync
            _writer = writer
            _presenter = presenter
            _fallback = fallback
            _queue = queue
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
            _queue = Nothing
            _targetServer = Nothing
            _targetDatabase = Nothing
            _seamsOverridden = False
            ConnectionFailedText = DefaultConnectionFailedText
            LogFailedText = DefaultLogFailedText
            ErrorCodeText = DefaultErrorCodeText
            LocalCodeText = DefaultLocalCodeText
        End SyncLock
    End Sub

    ' sqlEx may be Nothing (non-SQL exception): the SQL fields then stay Nothing.
    Private Shared Function BuildEntry(ex As Exception, sqlEx As SqlException, context As String,
                                       hint As String) As ErrorLogEntry
        Dim entry As New ErrorLogEntry With {
            .App = _appName,
            .AppVersion = _appVersion,
            .Usr = Environment.UserName,
            .Machine = Environment.MachineName,
            .Context = If(context, ""),
            .Reason = hint,
            .Message = If(ex.Message, ""),
            .ExceptionType = ex.GetType().FullName,
            .Trace = ex.ToString(),
            .TargetServer = _targetServer,
            .TargetDatabase = _targetDatabase
        }
        If sqlEx IsNot Nothing Then
            entry.SqlNumber = sqlEx.Number
            entry.SqlSeverity = sqlEx.Class
            entry.SqlState = sqlEx.State
            entry.SqlProcedure = If(sqlEx.Procedure, "")
            entry.SqlLine = sqlEx.LineNumber
            entry.SqlServer = If(sqlEx.Server, "")
        End If
        Return entry
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
