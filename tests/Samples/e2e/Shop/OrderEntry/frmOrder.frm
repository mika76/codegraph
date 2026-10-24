VERSION 5.00
Begin VB.Form frmOrder
   Caption         =   "Order entry"
   ClientHeight    =   3000
   ClientWidth     =   4500
   Begin VB.CommandButton cmdPlace
      Caption         =   "Place order"
      Height          =   375
      Left            =   120
      Top             =   120
      Width           =   1455
   End
End
Attribute VB_Name = "frmOrder"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit

Private Sub cmdPlace_Click()
    Dim rs As ADODB.Recordset
    Set rs = OpenRecordset("SELECT OrderId, Total FROM Orders WHERE CustomerId = 7")
    If Not rs.EOF Then
        ExecuteSql "EXEC sp_Order_Place @CustomerId = 7"
    End If
End Sub
