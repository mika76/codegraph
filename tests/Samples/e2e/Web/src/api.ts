export interface Order {
  orderId: number
  total: number
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init)
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json() as Promise<T>
}

export const api = {
  get: <T>(url: string) => request<T>(url),
}

export function getOrder(id: number): Promise<Order> {
  return fetch(`/orders/${id}`).then((r) => r.json())
}

export function loadOrder(id: number) {
  return api.get<Order>('/orders/' + id)
}
