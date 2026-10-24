using Microsoft.AspNetCore.Mvc;
using Shop.Api.Repositories;

namespace Shop.Api.Controllers;

[ApiController]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _orders;

    public OrdersController(IOrderRepository orders)
    {
        _orders = orders;
    }

    [HttpGet]
    [Route("/orders/{id}")]
    public IActionResult GetOrder(int id)
    {
        var order = _orders.GetById(id);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost]
    [Route("/orders")]
    public IActionResult Place(int customerId)
    {
        _orders.Place(customerId);
        return Ok();
    }
}
