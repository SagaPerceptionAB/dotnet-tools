import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';

interface Product {
  id: number;
  name: string;
}

export default function Products() {
  const [products, setProducts] = useState<Product[]>([]);

  useEffect(() => {
    fetch('/api/products')
      .then((r) => r.json())
      .then(setProducts);
  }, []);

  return (
    <section>
      <h1>Produkter</h1>
      <Link to="/products/new">Ny produkt</Link>
      <ul>
        {products.map((p) => (
          <li key={p.id}>
            <Link to={`/products/${p.id}`}>{p.name}</Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
