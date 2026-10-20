Option Strict On
Option Explicit On

Imports System.Data
Imports System.Data.SqlClient
Imports System.IO
Imports System.Threading

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

    Public Overrides Function ToString() As String
        Return String.Format("{0:yyyy-MM-dd HH:mm:ss} {1} {2} {3}@{4} [{5}] SQL {6}: {7}{8}{9}",
                             DateTime.Now, App, AppVersion, Usr, Machine, Context,
                             If(SqlNumber.HasValue, SqlNumber.Value.ToString(), "-"),
                             Message, Environment.NewLine, Trace)
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
