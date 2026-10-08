Option Strict On
Option Explicit On

Imports System.Data.SqlClient
Imports System.Reflection

''' <summary>
''' Builds real SqlException instances (the type has no public constructor) through reflection
''' on the .NET Framework 4.8 internals: SqlError ctor, SqlErrorCollection.Add and
''' SqlException.CreateException.
''' </summary>
Friend NotInheritable Class SqlExceptionFactory

    Private Const Flags As BindingFlags = BindingFlags.Instance Or BindingFlags.Static Or BindingFlags.Public Or BindingFlags.NonPublic

    Private Sub New()
    End Sub

    Friend Shared Function Create(number As Integer,
                                  Optional errorClass As Byte = 16,
                                  Optional state As Byte = 1,
                                  Optional procedure As String = Nothing,
                                  Optional lineNumber As Integer = 1,
                                  Optional server As String = "TESTSERVER",
                                  Optional message As String = "Test SQL error") As SqlException
        Dim sqlAssembly As Assembly = GetType(SqlException).Assembly
        Dim errorType As Type = GetType(SqlError)
        Dim collectionType As Type = GetType(SqlErrorCollection)

        ' Internal ctor: (Integer infoNumber, Byte errorState, Byte errorClass, String server,
        ' String errorMessage, String procedure, Integer lineNumber [, UInteger win32ErrorCode [, Exception]]).
        ' Take the shortest one whose first seven parameters match.
        Dim errorCtor As ConstructorInfo = Nothing
        For Each candidate As ConstructorInfo In errorType.GetConstructors(Flags)
            Dim p As ParameterInfo() = candidate.GetParameters()
            If p.Length >= 7 AndAlso
               p(0).ParameterType Is GetType(Integer) AndAlso
               p(1).ParameterType Is GetType(Byte) AndAlso
               p(2).ParameterType Is GetType(Byte) AndAlso
               p(3).ParameterType Is GetType(String) AndAlso
               p(4).ParameterType Is GetType(String) AndAlso
               p(5).ParameterType Is GetType(String) AndAlso
               p(6).ParameterType Is GetType(Integer) Then
                If errorCtor Is Nothing OrElse p.Length < errorCtor.GetParameters().Length Then
                    errorCtor = candidate
                End If
            End If
        Next
        If errorCtor Is Nothing Then
            Throw New InvalidOperationException("SqlError constructor not found.")
        End If

        Dim errorArgs As New List(Of Object) From {number, state, errorClass, server, message, procedure, lineNumber}
        For i As Integer = 7 To errorCtor.GetParameters().Length - 1
            Dim pt As Type = errorCtor.GetParameters()(i).ParameterType
            errorArgs.Add(If(pt.IsValueType, Activator.CreateInstance(pt), Nothing))
        Next
        Dim sqlError As Object = errorCtor.Invoke(errorArgs.ToArray())

        Dim collection As Object = Activator.CreateInstance(collectionType, True)
        Dim addMethod As MethodInfo = collectionType.GetMethod("Add", Flags, Nothing, New Type() {errorType}, Nothing)
        If addMethod Is Nothing Then
            Throw New InvalidOperationException("SqlErrorCollection.Add not found.")
        End If
        addMethod.Invoke(collection, New Object() {sqlError})

        ' Internal static CreateException(SqlErrorCollection, String [, Guid]).
        Dim createMethod As MethodInfo = Nothing
        For Each candidate As MethodInfo In GetType(SqlException).GetMethods(Flags)
            If candidate.Name <> "CreateException" Then Continue For
            Dim p As ParameterInfo() = candidate.GetParameters()
            If p.Length >= 2 AndAlso p(0).ParameterType Is collectionType AndAlso p(1).ParameterType Is GetType(String) Then
                If p.Length = 2 OrElse (p.Length = 3 AndAlso p(2).ParameterType Is GetType(Guid)) Then
                    If createMethod Is Nothing OrElse p.Length < createMethod.GetParameters().Length Then
                        createMethod = candidate
                    End If
                End If
            End If
        Next
        If createMethod Is Nothing Then
            Throw New InvalidOperationException("SqlException.CreateException not found.")
        End If

        Dim createArgs As New List(Of Object) From {collection, "11.0.0"}
        If createMethod.GetParameters().Length = 3 Then
            createArgs.Add(Guid.NewGuid())
        End If
        Return DirectCast(createMethod.Invoke(Nothing, createArgs.ToArray()), SqlException)
    End Function

End Class
