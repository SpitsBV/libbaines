Option Strict On
Option Explicit On

Imports System.Collections.Concurrent
Imports System.Threading

' Thread-safe recording fakes for the ErrorLog test seams.

Friend NotInheritable Class FakeWriter
    Implements IErrorLogWriter

    Private ReadOnly _entries As New ConcurrentQueue(Of ErrorLogEntry)
    Private _nextId As Integer

    ''' <summary>Id returned by every Write.</summary>
    Friend Property ResultId As Integer = 42
    Friend Property ResultText As String = "T"

    ''' <summary>When set, Write throws this instead of returning a result (the call is still recorded).</summary>
    Friend Property ThrowOnWrite As Exception

    ''' <summary>When set, runs inside Write before the result is returned (re-entrancy tests).</summary>
    Friend Property OnWrite As Action(Of ErrorLogEntry)

    ''' <summary>When True, ids are 1, 2, 3... instead of ResultId.</summary>
    Friend Property Sequential As Boolean

    Friend ReadOnly Property Entries As ErrorLogEntry()
        Get
            Return _entries.ToArray()
        End Get
    End Property

    Friend ReadOnly Property CallCount As Integer
        Get
            Return _entries.Count
        End Get
    End Property

    Public Function Write(entry As ErrorLogEntry) As ErrorLogResult Implements IErrorLogWriter.Write
        _entries.Enqueue(entry)
        Dim hook As Action(Of ErrorLogEntry) = OnWrite
        If hook IsNot Nothing Then hook(entry)
        Dim toThrow As Exception = ThrowOnWrite
        If toThrow IsNot Nothing Then Throw toThrow
        If Sequential Then
            Return New ErrorLogResult(Interlocked.Increment(_nextId), ResultText)
        End If
        Return New ErrorLogResult(ResultId, ResultText)
    End Function

End Class

Friend NotInheritable Class FakePresenter
    Implements IErrorPresenter

    Private ReadOnly _shown As New ConcurrentQueue(Of String)

    Friend Property ThrowOnShow As Exception

    Friend ReadOnly Property Shown As String()
        Get
            Return _shown.ToArray()
        End Get
    End Property

    Friend ReadOnly Property CallCount As Integer
        Get
            Return _shown.Count
        End Get
    End Property

    Public Sub Show(text As String) Implements IErrorPresenter.Show
        _shown.Enqueue(text)
        Dim toThrow As Exception = ThrowOnShow
        If toThrow IsNot Nothing Then Throw toThrow
    End Sub

End Class

Friend NotInheritable Class FakeFallback
    Implements IFallbackLog

    Private ReadOnly _written As New ConcurrentQueue(Of String)

    Friend Property ThrowOnWrite As Exception

    Friend ReadOnly Property Written As String()
        Get
            Return _written.ToArray()
        End Get
    End Property

    Friend ReadOnly Property CallCount As Integer
        Get
            Return _written.Count
        End Get
    End Property

    Public Sub Write(text As String) Implements IFallbackLog.Write
        _written.Enqueue(text)
        Dim toThrow As Exception = ThrowOnWrite
        If toThrow IsNot Nothing Then Throw toThrow
    End Sub

End Class
