import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import axios from 'axios';

export default function ProductDetail() {
  const { id } = useParams();
  const [product, setProduct] = useState<{ name: string } | null>(null);

  useEffect(() => {
    axios.get(`/api/products/${id}`).then((r) => setProduct(r.data));
  }, [id]);

  const remove = () => axios.delete(`/api/products/${id}`);

  return (
    <article>
      <h1>{product?.name}</h1>
      <button onClick={remove}>Ta bort</button>
    </article>
  );
}
