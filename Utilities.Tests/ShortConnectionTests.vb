Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' CH-238 - the heartbeat and the error log writer connect over TCP only, so their short
' ConnectTimeout is not exceeded by the named-pipe fallback when the database is unreachable.
<TestClass>
Public Class ShortConnectionTests

    Private Shared Function DataSourceOf(connectionString As String) As String
        Return New SqlConnectionStringBuilder(connectionString).DataSource
    End Function

    <TestMethod>
    Public Sub ServerName_GetsTcpPrefix_AndTimeout()
        Dim result As String = ShortConnection.Build("Data Source=sluchdb02;Initial Catalog=UPLUS2;Integrated Security=True", 3)
        Dim builder As New SqlConnectionStringBuilder(result)

        Assert.AreEqual("tcp:sluchdb02", builder.DataSource)
        Assert.AreEqual(3, builder.ConnectTimeout)
        Assert.AreEqual("UPLUS2", builder.InitialCatalog)
        Assert.IsTrue(builder.IntegratedSecurity)
    End Sub

    <TestMethod>
    Public Sub NamedInstanceAndPort_GetTcpPrefix()
        Assert.AreEqual("tcp:SLUCHDB02\UCHQA", DataSourceOf(ShortConnection.Build("Data Source=SLUCHDB02\UCHQA", 5)))
        Assert.AreEqual("tcp:192.168.65.1,1433", DataSourceOf(ShortConnection.Build("Data Source=192.168.65.1,1433", 5)))
    End Sub

    <TestMethod>
    Public Sub ExplicitProtocol_IsKept()
        Assert.AreEqual("tcp:sluchdb02", DataSourceOf(ShortConnection.Build("Data Source=tcp:sluchdb02", 3)))
        Assert.AreEqual("np:sluchdb02", DataSourceOf(ShortConnection.Build("Data Source=np:sluchdb02", 3)))
        Assert.AreEqual("TCP:sluchdb02", DataSourceOf(ShortConnection.Build("Data Source=TCP:sluchdb02", 3)))
    End Sub

    <TestMethod>
    Public Sub LocalAliases_AreKept()
        Assert.AreEqual(".", DataSourceOf(ShortConnection.Build("Data Source=.", 3)))
        Assert.AreEqual("(local)", DataSourceOf(ShortConnection.Build("Data Source=(local)", 3)))
        Assert.AreEqual("(localdb)\MSSQLLocalDB", DataSourceOf(ShortConnection.Build("Data Source=(localdb)\MSSQLLocalDB", 3)))
    End Sub

End Class
