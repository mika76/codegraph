namespace Shop.Api.Repositories;

public interface IOrderRepository
{
    OrderDto? GetById(int id);
    void Place(int customerId);
}

public record OrderDto(int OrderId, decimal Total);
