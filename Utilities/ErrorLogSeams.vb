Option Strict On
Option Explicit On

Imports System.Data
Imports System.Data.SqlClient
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Web.Script.Serialization

' CH-227: building blocks of ErrorLog. Friend so the behaviour can be tested through fakes
' (InternalsVisibleTo Utilities.Tests) without widening the public API.

Friend Interface IErrorLogWriter
    Function Write(entry As ErrorLogEntry) As ErrorLogResult
End Interface

Friend Interface IErrorPresenter
    Sub Show(text As String)
End Interface

Friend Interface IFallbackLog
    Sub Write(text As String)
End Interface

Friend NotInheritable Class ErrorLogEntry
    Public Property App As String
    Public Property AppVersion As String
    Public Property Usr As String
    Public Property Machine As String
    Public Property Context As String
    ' CH-238: the hint passed to Report, Nothing when there was none.
    Public Property Reason As String
    Public Property Message As String
    Public Property ExceptionType As String
    Public Property Trace As String
    Public Property SqlNumber As Integer?
    Public Property SqlSeverity As Byte?
    Public Property SqlState As Byte?
    Public Property SqlProcedure As String
    Public Property SqlLine As Integer?
    Public Property SqlServer As String
    ' CH-238: set only when the database write failed and the entry was queued locally. Created is
    ' the moment of the error; Nothing on a direct write, where the database default applies.
    Public Property LocalRef As String
    Public Property Created As DateTime?
    ' CH-238: DataSource and InitialCatalog of the configured connection, so a queued entry is
    ' replayed only into the database it was meant for.
    Public Property TargetServer As String
    Public Property TargetDatabase As String

    ' Readable fallback text. Shows the local code and the reason when there are any, so the
    ' administrator reading the file can match the code the user passes on.
    Public Overrides Function ToString() As String
        Dim text As String = String.Format("{0:yyyy-MM-dd HH:mm:ss} {1} {2} {3}@{4} [{5}] SQL {6}: {7}{8}{9}",
                             If(Created, DateTime.Now), App, AppVersion, Usr, Machine, Context,
                             If(SqlNumber.HasValue, SqlNumber.Value.ToString(), "-"),
                             Message, Environment.NewLine, Trace)
        If Not String.IsNullOrEmpty(Reason) Then text = "Reason: " & Reason & Environment.NewLine & text
        If Not String.IsNullOrEmpty(LocalRef) Then text = "Local code: " & LocalRef & Environment.NewLine & text
        Return text
    End Function
End Class

Friend NotInheritable Class ErrorLogResult
    Public Sub New(errorLogId As Integer, userMessage As String)
        Me.ErrorLogId = errorLogId
        Me.UserMessage = userMessage
    End Sub
    Public ReadOnly Property ErrorLogId As Integer
    Public ReadOnly Property UserMessage As String
End Class

' Writes through dbo.p_insert_error_log on its own short-timeout connection, so a failing or busy
' application connection (designer adapters, the version check in GetConnectionString) is never reused.
Friend NotInheritable Class SqlErrorLogWriter
    Implements IErrorLogWriter

    Private ReadOnly _connectionString As String

    Public Sub New(connectionString As String)
        Dim builder As New SqlConnectionStringBuilder(connectionString) With {.ConnectTimeout = 5}
        _connectionString = builder.ConnectionString
    End Sub

    Public Function Write(entry As ErrorLogEntry) As ErrorLogResult Implements IErrorLogWriter.Write
        Using conn As New SqlConnection(_connectionString),
              cmd As New SqlCommand("dbo.p_insert_error_log", conn)
            cmd.CommandType = CommandType.StoredProcedure
            cmd.CommandTimeout = 10
            cmd.Parameters.Add("@app", SqlDbType.NVarChar, 50).Value = entry.App
            cmd.Parameters.Add("@app_version", SqlDbType.NVarChar, 50).Value = entry.AppVersion
            cmd.Parameters.Add("@usr", SqlDbType.NVarChar, 128).Value = entry.Usr
            cmd.Parameters.Add("@machine", SqlDbType.NVarChar, 128).Value = entry.Machine
            cmd.Parameters.Add("@context", SqlDbType.NVarChar, 250).Value = Truncate(entry.Context, 250)
            cmd.Parameters.Add("@reason", SqlDbType.NVarChar, 1000).Value =
                If(String.IsNullOrEmpty(entry.Reason), DBNull.Value, CObj(Truncate(entry.Reason, 1000)))
            cmd.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = entry.Message
            cmd.Parameters.Add("@exception_type", SqlDbType.NVarChar, 250).Value = DbValue(entry.ExceptionType)
            cmd.Parameters.Add("@trace", SqlDbType.NVarChar, -1).Value = DbValue(entry.Trace)
            cmd.Parameters.Add("@sql_number", SqlDbType.Int).Value = DbValue(entry.SqlNumber)
            cmd.Parameters.Add("@sql_severity", SqlDbType.TinyInt).Value = DbValue(entry.SqlSeverity)
            cmd.Parameters.Add("@sql_state", SqlDbType.TinyInt).Value = DbValue(entry.SqlState)
            cmd.Parameters.Add("@sql_procedure", SqlDbType.NVarChar, 128).Value = DbValue(entry.SqlProcedure)
            cmd.Parameters.Add("@sql_line", SqlDbType.Int).Value = DbValue(entry.SqlLine)
            cmd.Parameters.Add("@sql_server", SqlDbType.NVarChar, 128).Value = DbValue(entry.SqlServer)
            conn.Open()
            Using reader As SqlDataReader = cmd.ExecuteReader()
                If Not reader.Read() Then
                    Throw New InvalidOperationException("p_insert_error_log returned no row.")
                End If
                Return New ErrorLogResult(reader.GetInt32(0), reader.GetString(1))
            End Using
        End Using
    End Function

    Private Shared Function DbValue(value As Object) As Object
        Return If(value, CObj(DBNull.Value))
    End Function

    Private Shared Function Truncate(value As String, length As Integer) As String
        If value Is Nothing OrElse value.Length <= length Then Return value
        Return value.Substring(0, length)
    End Function
End Class

' Shows text on the UI thread. Called from a worker thread it posts to the captured UI context,
' so a BackgroundWorker never touches a control or blocks on the UI.
Friend NotInheritable Class MessageBoxPresenter
    Implements IErrorPresenter

    Private ReadOnly _uiContext As SynchronizationContext

    Public Sub New(uiContext As SynchronizationContext)
        _uiContext = uiContext
    End Sub

    Public Sub Show(text As String) Implements IErrorPresenter.Show
        If _uiContext Is Nothing OrElse SynchronizationContext.Current Is _uiContext Then
            ShowBox(text)
        Else
            _uiContext.Post(Sub(state) ShowBox(CStr(state)), text)
        End If
    End Sub

    ' MsgBox without a title would caption the dialog with this assembly (Utilities); keep the
    ' application's name, as the MsgBox call in the application itself did.
    Private Shared Sub ShowBox(text As String)
        Dim entry As Reflection.Assembly = Reflection.Assembly.GetEntryAssembly()
        If entry Is Nothing Then
            MsgBox(text)
        Else
            MsgBox(text, MsgBoxStyle.OkOnly, entry.GetName().Name)
        End If
    End Sub
End Class

' Last-resort log when the database cannot take the entry. Per-user folder: the application folder
' (where clLogging writes) is often not writable for users.
Friend NotInheritable Class FileFallbackLog
    Implements IFallbackLog

    Private Shared ReadOnly _lock As New Object()
    Private ReadOnly _appName As String

    Public Sub New(appName As String)
        _appName = If(String.IsNullOrEmpty(appName), "Utilities", appName)
    End Sub

    Public Sub Write(text As String) Implements IFallbackLog.Write
        Dim folder As String = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _appName, "Logfiles")
        Directory.CreateDirectory(folder)
        Dim file As String = Path.Combine(folder, String.Format("ErrorLog_{0:yyyyMMdd}.txt", DateTime.Now))
        SyncLock _lock
            IO.File.AppendAllText(file, text & Environment.NewLine & Environment.NewLine)
        End SyncLock
    End Sub
End Class

' CH-238: entries whose database write failed, kept on this computer until they can be replayed.
Friend Interface IPendingQueue
    ' Throws when the queue file cannot be locked.
    Sub Append(entry As ErrorLogEntry)
    ' Corrupt lines come back with Entry = Nothing.
    Function ReadAll() As PendingLine()
    ' Under one lock: appends rejected (with a timestamp) to the rejected file and removes one
    ' occurrence per processed or rejected line (matched on Raw) from the queue. Lines appended
    ' after ReadAll stay in the queue.
    Sub Remove(processed As PendingLine(), rejected As PendingLine())
End Interface

Friend NotInheritable Class PendingLine
    Public Property Raw As String
    ' Nothing when Raw is not a valid entry.
    Public Property Entry As ErrorLogEntry
End Class

' One JSON object per line in %LOCALAPPDATA%\<app>\Logfiles\ErrorLog_pending.jsonl. Two processes of
' the same Windows user (Citrix, production and QA side by side) share the file, so every open is
' exclusive (FileShare.None) with a short retry instead of only an in-process lock.
Friend NotInheritable Class FilePendingQueue
    Implements IPendingQueue

    Private Const Retries As Integer = 5
    Private Const RetryDelayMs As Integer = 100

    Private ReadOnly _file As String
    Private ReadOnly _rejectedFile As String

    Public Sub New(appName As String)
        Me.New(New DirectoryInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            If(String.IsNullOrEmpty(appName), "Utilities", appName), "Logfiles")))
    End Sub

    ' Tests: a queue in any folder.
    Friend Sub New(folder As DirectoryInfo)
        _file = Path.Combine(folder.FullName, "ErrorLog_pending.jsonl")
        _rejectedFile = Path.Combine(folder.FullName, "ErrorLog_pending_rejected.txt")
    End Sub

    Public Sub Append(entry As ErrorLogEntry) Implements IPendingQueue.Append
        Dim line As String = NewSerializer().Serialize(entry)
        Using stream As FileStream = OpenExclusive(_file, FileMode.Append, FileAccess.Write),
              writer As New StreamWriter(stream)
            writer.WriteLine(line)
        End Using
    End Sub

    Public Function ReadAll() As PendingLine() Implements IPendingQueue.ReadAll
        If Not IO.File.Exists(_file) Then Return New PendingLine() {}
        Dim serializer As JavaScriptSerializer = NewSerializer()
        Dim lines As New List(Of PendingLine)
        Using stream As FileStream = OpenExclusive(_file, FileMode.Open, FileAccess.Read),
              reader As New StreamReader(stream)
            Dim raw As String = reader.ReadLine()
            While raw IsNot Nothing
                If raw.Trim().Length > 0 Then
                    lines.Add(New PendingLine With {.Raw = raw, .Entry = Parse(serializer, raw)})
                End If
                raw = reader.ReadLine()
            End While
        End Using
        Return lines.ToArray()
    End Function

    Public Sub Remove(processed As PendingLine(), rejected As PendingLine()) Implements IPendingQueue.Remove
        ' One exclusive handle on the queue for the whole operation, so an Append by another thread
        ' or process cannot slip in between reading and writing back.
        Using stream As FileStream = OpenExclusive(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite)
            If rejected.Length > 0 Then
                Using rejectedStream As FileStream = OpenExclusive(_rejectedFile, FileMode.Append, FileAccess.Write),
                      writer As New StreamWriter(rejectedStream)
                    Dim stamp As String = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
                    For Each line As PendingLine In rejected
                        writer.WriteLine(stamp & " " & line.Raw)
                    Next
                End Using
            End If

            Dim remaining As New List(Of String)
            Using reader As New StreamReader(stream, New Text.UTF8Encoding(False), True, 4096, True)
                Dim raw As String = reader.ReadLine()
                While raw IsNot Nothing
                    If raw.Trim().Length > 0 Then remaining.Add(raw)
                    raw = reader.ReadLine()
                End While
            End Using
            ' Multiset match: each processed or rejected line removes one occurrence.
            For Each line As PendingLine In processed.Concat(rejected)
                remaining.Remove(line.Raw)
            Next

            stream.SetLength(0)
            stream.Position = 0
            Using writer As New StreamWriter(stream, New Text.UTF8Encoding(False), 4096, True)
                For Each raw As String In remaining
                    writer.WriteLine(raw)
                Next
            End Using
        End Using
    End Sub

    Private Shared Function NewSerializer() As JavaScriptSerializer
        Return New JavaScriptSerializer() With {.MaxJsonLength = Integer.MaxValue}
    End Function

    Private Shared Function Parse(serializer As JavaScriptSerializer, raw As String) As ErrorLogEntry
        Try
            Dim entry As ErrorLogEntry = serializer.Deserialize(Of ErrorLogEntry)(raw)
            ' JavaScriptSerializer writes DateTime as UTC; Created is local time.
            If entry IsNot Nothing AndAlso entry.Created.HasValue Then entry.Created = entry.Created.Value.ToLocalTime()
            Return entry
        Catch ex As Exception
            ' ErrorLog: bewust genegeerd, a corrupt line is returned with Entry = Nothing for the caller to reject.
            Return Nothing
        End Try
    End Function

    ' Exclusive open with retries; rethrows the last IOException when the file stays locked.
    Private Shared Function OpenExclusive(file As String, mode As FileMode, access As FileAccess) As FileStream
        Directory.CreateDirectory(Path.GetDirectoryName(file))
        Dim attempt As Integer = 0
        Do
            Try
                Return New FileStream(file, mode, access, FileShare.None)
            Catch ex As IOException When attempt < Retries
                attempt += 1
                Thread.Sleep(RetryDelayMs)
            End Try
        Loop
    End Function
End Class
