Option Strict On
Option Explicit On

Imports System.Data.SqlClient

''' <summary>
''' CH-238: back-off of the MainForm heartbeat, which reads b_semaphore every 5 seconds on the UI
''' thread. After a lost connection the user is told once, and the heartbeat leaves the database
''' alone for a period so the UI is not blocked by a connect timeout on every tick. UI thread only.
''' </summary>
Friend NotInheritable Class HeartbeatBackoff

    Private ReadOnly _period As TimeSpan
    Private _failing As Boolean
    Private _resumeAt As DateTime

    Public Sub New(period As TimeSpan)
        _period = period
    End Sub

    ''' <summary>True while the last read failed and the period since has not passed.</summary>
    Public Function ShouldSkip(now As DateTime) As Boolean
        Return _failing AndAlso now < _resumeAt
    End Function

    ''' <summary>Starts a new period. True only on the first failure since the last success.</summary>
    Public Function Failed(now As DateTime) As Boolean
        Dim first As Boolean = Not _failing
        _failing = True
        _resumeAt = now + _period
        Return first
    End Function

    ''' <summary>
    ''' True from a connection failure until the next successful read, also after the period has
    ''' passed. The auto-logout read uses it to run only after a successful semaphore read.
    ''' </summary>
    Public ReadOnly Property IsFailing As Boolean
        Get
            Return _failing
        End Get
    End Property

    Public Sub Succeeded()
        _failing = False
    End Sub

    ''' <summary>
    ''' Catch of every heartbeat read (semaphores, auto logout): backs off on a connection failure and
    ''' reports the first one of an outage without writing to the database. False for other errors.
    ''' </summary>
    Public Function OnFailure(ex As Exception, now As DateTime, context As String) As Boolean
        If Not IsConnectionFailure(ex) Then Return False
        If Failed(now) AndAlso ErrorLog.IsEnabled Then ErrorLog.ReportConnectionLost(ex, context)
        Return True
    End Function

    ''' <summary>
    ''' A connection error as ErrorLog classifies it, or a timeout (-2): either way the heartbeat
    ''' blocked the UI and should back off.
    ''' </summary>
    Friend Shared Function IsConnectionFailure(ex As Exception) As Boolean
        Dim sqlEx As SqlException = ErrorLog.FindSqlException(ex)
        Return sqlEx IsNot Nothing AndAlso (sqlEx.Number = -2 OrElse ErrorLog.IsConnectionError(sqlEx))
    End Function

End Class
