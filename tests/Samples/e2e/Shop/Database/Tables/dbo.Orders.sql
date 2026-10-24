CREATE TABLE [dbo].[Orders]
(
[OrderId] [int] NOT NULL IDENTITY(1, 1),
[CustomerId] [int] NOT NULL,
[Total] [decimal] (18, 2) NOT NULL,
[PlacedAt] [datetime] NOT NULL
)
GO
ALTER TABLE [dbo].[Orders] ADD CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId])
GO
ALTER TABLE [dbo].[Orders] ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customers] ([CustomerId])
GO
