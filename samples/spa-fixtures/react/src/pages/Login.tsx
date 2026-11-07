import { useEffect } from 'react';

export default function Login() {
  useEffect(() => {
    document.title = 'Logga in';
  }, []);

  const submit = () => {
    fetch('/api/auth/login', { method: 'POST' });
  };

  return (
    <main>
      <h1>Logga in</h1>
      <form onSubmit={submit}>
        <label htmlFor="email">E-post</label>
        <input id="email" name="email" type="email" required />

        <label htmlFor="password">Lösenord</label>
        <input id="password" name="password" type="password" required minLength={8} />

        <button type="submit">Logga in</button>
      </form>
    </main>
  );
}
