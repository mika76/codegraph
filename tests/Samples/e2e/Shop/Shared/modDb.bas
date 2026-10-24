Attribute VB_Name = "modDb"
Option Explicit

Public Const MAX_ORDER_LINES = 50

Public Enum OrderStatus
    osPlaced = 1
    osShipped
End Enum

Public Function OpenRecordset(ByVal sql As String) As ADODB.Recordset
    Dim rs As New ADODB.Recordset
    rs.Open sql, GetConnection()
    Set OpenRecordset = rs
End Function

Public Sub ExecuteSql(ByVal sql As String)
    GetConnection().Execute sql
End Sub

Private Function GetConnection() As ADODB.Connection
    Static cn As ADODB.Connection
    If cn Is Nothing Then
        Set cn = New ADODB.Connection
        cn.Open "Provider=SQLOLEDB;Data Source=.;Initial Catalog=Shop;Integrated Security=SSPI"
    End If
    Set GetConnection = cn
End Function
