import axios from 'axios';

export function addToCart(id: string | string[]) {
  return axios.post(`/api/cart/${id}`);
}
