SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[sp_Order_Place]
    @CustomerId int
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId = @CustomerId)
        RETURN -1;

    INSERT INTO dbo.Orders (CustomerId, Total, PlacedAt)
    VALUES (@CustomerId, 0, GETDATE());
END
GO
