Option Strict On
Option Explicit On

Imports System.Collections.Concurrent
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-227 - MessageBoxPresenter must marshal to the UI context when called from another thread.
' The recording context never runs the callbacks, so no real MessageBox is ever shown.
<TestClass>
Public Class PresenterMarshallingTests

    Private NotInheritable Class RecordingContext
        Inherits SynchronizationContext

        Friend ReadOnly Posts As New ConcurrentQueue(Of Tuple(Of SendOrPostCallback, Object))
        Friend ReadOnly Sends As New ConcurrentQueue(Of Tuple(Of SendOrPostCallback, Object))

        Public Overrides Sub Post(d As SendOrPostCallback, state As Object)
            Posts.Enqueue(Tuple.Create(d, state))
        End Sub

        Public Overrides Sub Send(d As SendOrPostCallback, state As Object)
            Sends.Enqueue(Tuple.Create(d, state))
        End Sub

        Public Overrides Function CreateCopy() As SynchronizationContext
            Return Me
        End Function
    End Class

    <TestMethod>
    Public Sub Show_FromWorkerThread_PostsToUiContext_NeverSends()
        Dim ui As New RecordingContext()
        Dim presenter As IErrorPresenter = New MessageBoxPresenter(ui)

        Dim workerThreadId As Integer = 0
        Dim caught As Exception = Nothing
        Task.Run(Sub()
                     workerThreadId = Thread.CurrentThread.ManagedThreadId
                     Try
                         presenter.Show("hello from worker")
                     Catch ex As Exception
                         caught = ex
                     End Try
                 End Sub).Wait()

        Assert.IsNull(caught, "Show must not throw or show a dialog on the worker thread")
        Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, workerThreadId)
        Assert.AreEqual(1, ui.Posts.Count, "exactly one Post to the UI context")
        Assert.AreEqual(0, ui.Sends.Count, "Send must never be used")
    End Sub

    <TestMethod>
    Public Sub Show_FromTwoWorkerThreads_PostsOncePerCall()
        Dim ui As New RecordingContext()
        Dim presenter As IErrorPresenter = New MessageBoxPresenter(ui)

        Task.WaitAll(Task.Run(Sub() presenter.Show("a")), Task.Run(Sub() presenter.Show("b")))

        Assert.AreEqual(2, ui.Posts.Count)
        Assert.AreEqual(0, ui.Sends.Count)
    End Sub

    <TestMethod>
    Public Sub Show_FromWorkerThread_PostedCallbackIsNotNull()
        Dim ui As New RecordingContext()
        Dim presenter As IErrorPresenter = New MessageBoxPresenter(ui)

        Task.Run(Sub() presenter.Show("hello")).Wait()

        Dim posted As Tuple(Of SendOrPostCallback, Object) = Nothing
        Assert.IsTrue(ui.Posts.TryPeek(posted))
        Assert.IsNotNull(posted.Item1)
    End Sub

    ' Note: the "called on the UI context thread shows directly" and "uiContext Nothing shows directly"
    ' branches would open a real MessageBox, so they are deliberately not tested here.

End Class
