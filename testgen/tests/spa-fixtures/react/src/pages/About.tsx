import { Link } from 'react-router-dom';

export default function About() {
  return (
    <main>
      <title>Om butiken</title>
      <h1>Om butiken</h1>
      <Link to="/contact">Kontakta oss</Link>
      <Link to="/help/faq">Vanliga frågor</Link>
    </main>
  );
}
