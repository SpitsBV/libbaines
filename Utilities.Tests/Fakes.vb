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

Friend NotInheritable Class FakePendingQueue
    Implements IPendingQueue

    Private ReadOnly _sync As New Object()
    Private ReadOnly _lines As New List(Of PendingLine)
    Private ReadOnly _rejected As New List(Of PendingLine)
    Private ReadOnly _removeCalls As New List(Of Tuple(Of PendingLine(), PendingLine()))
    Private _nextLine As Integer

    ''' <summary>When set, Append throws this instead of queueing (a locked queue file).</summary>
    Friend Property ThrowOnAppend As Exception

    ''' <summary>Entries currently in the queue, in order.</summary>
    Friend ReadOnly Property Entries As ErrorLogEntry()
        Get
            SyncLock _sync
                Return _lines.Select(Function(l) l.Entry).ToArray()
            End SyncLock
        End Get
    End Property

    Friend ReadOnly Property Rejected As PendingLine()
        Get
            SyncLock _sync
                Return _rejected.ToArray()
            End SyncLock
        End Get
    End Property

    ''' <summary>Every Remove call: Item1 = processed, Item2 = rejected.</summary>
    Friend ReadOnly Property RemoveCalls As Tuple(Of PendingLine(), PendingLine())()
        Get
            SyncLock _sync
                Return _removeCalls.ToArray()
            End SyncLock
        End Get
    End Property

    ''' <summary>Adds a line as it would be in the file (for example a corrupt one with Entry = Nothing).</summary>
    Friend Sub AddLine(line As PendingLine)
        SyncLock _sync
            _lines.Add(line)
        End SyncLock
    End Sub

    Public Sub Append(entry As ErrorLogEntry) Implements IPendingQueue.Append
        Dim toThrow As Exception = ThrowOnAppend
        If toThrow IsNot Nothing Then Throw toThrow
        SyncLock _sync
            _nextLine += 1
            _lines.Add(New PendingLine With {.Raw = "line " & _nextLine.ToString(), .Entry = entry})
        End SyncLock
    End Sub

    Public Function ReadAll() As PendingLine() Implements IPendingQueue.ReadAll
        SyncLock _sync
            Return _lines.ToArray()
        End SyncLock
    End Function

    ' Same semantics as FilePendingQueue.Remove: one occurrence per Raw match is removed.
    Public Sub Remove(processed As PendingLine(), rejected As PendingLine()) Implements IPendingQueue.Remove
        SyncLock _sync
            _removeCalls.Add(Tuple.Create(processed, rejected))
            _rejected.AddRange(rejected)
            For Each line As PendingLine In processed.Concat(rejected)
                Dim index As Integer = _lines.FindIndex(Function(l) l.Raw = line.Raw)
                If index >= 0 Then _lines.RemoveAt(index)
            Next
        End SyncLock
    End Sub

End Class
