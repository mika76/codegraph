using System.Data;
using System.Data.SqlClient;

namespace Shop.Api.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly string _connectionString;

    public OrderRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public OrderDto? GetById(int id)
    {
        using var cn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand("SELECT OrderId, Total FROM Orders WHERE OrderId = @id", cn);
        cmd.Parameters.AddWithValue("@id", id);
        cn.Open();
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? new OrderDto(rd.GetInt32(0), rd.GetDecimal(1)) : null;
    }

    public void Place(int customerId)
    {
        using var cn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand("sp_Order_Place", cn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        cn.Open();
        cmd.ExecuteNonQuery();
    }
}
