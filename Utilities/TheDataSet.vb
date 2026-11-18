

Partial Public Class TheDataSet
End Class

Namespace TheDataSetTableAdapters
    
    Partial Public Class m_formTableAdapter
    End Class

    Partial Public Class b_semaphoreTableAdapter
        ' CH-238: the heartbeat runs on the UI thread; a lost connection must not block it for the
        ' default 30 seconds.
        Friend Sub SetCommandTimeout(seconds As Integer)
            For Each cmd As Global.System.Data.SqlClient.SqlCommand In Me.CommandCollection
                cmd.CommandTimeout = seconds
            Next
        End Sub
    End Class
End Namespace
