CREATE TABLE [dbo].[Customers]
(
[CustomerId] [int] NOT NULL IDENTITY(1, 1),
[Name] [nvarchar] (100) NOT NULL,
[Email] [nvarchar] (200) NULL
)
GO
ALTER TABLE [dbo].[Customers] ADD CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([CustomerId])
GO
