export default function Contact() {
  return (
    <main>
      <h1>Kontakt</h1>
      <form action="/api/contact" method="post">
        <label htmlFor="from">Din e-post</label>
        <input id="from" type="email" required />
        <label htmlFor="message">Meddelande</label>
        <textarea id="message" required maxLength={1000} />
        <button>Skicka</button>
      </form>
    </main>
  );
}
