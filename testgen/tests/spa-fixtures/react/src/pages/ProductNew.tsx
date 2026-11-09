import { FormEvent } from 'react';

export default function ProductNew() {
  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = Object.fromEntries(new FormData(e.currentTarget));
    await fetch('/api/products', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data),
    });
  }

  return (
    <section>
      <h1>Ny produkt</h1>
      <form onSubmit={save}>
        <label htmlFor="name">Namn</label>
        <input id="name" name="name" type="text" required minLength={3} maxLength={50} />

        <label htmlFor="price">Pris</label>
        <input id="price" name="price" type="number" required min="0" max="10000" />

        <label htmlFor="category">Kategori</label>
        <select id="category" name="category">
          <option value="">Välj</option>
          <option value="books">Böcker</option>
          <option value="games">Spel</option>
        </select>

        <label>
          Beskrivning
          <textarea name="description" maxLength={500} />
        </label>

        <button type="submit">Spara produkt</button>
      </form>
    </section>
  );
}
