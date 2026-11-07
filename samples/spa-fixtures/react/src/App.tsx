import { Routes, Route } from 'react-router-dom';
import About from './pages/About';
import Contact from './pages/Contact';
import Help from './pages/Help';
import Faq from './pages/Faq';
import NotFound from './pages/NotFound';

// Äldre delar av appen som routas med JSX.
export default function App() {
  return (
    <Routes>
      <Route path="/about" element={<About />} />
      <Route path="/contact" element={<Contact />} />
      <Route path="/help">
        <Route index element={<Help />} />
        <Route path="faq" element={<Faq />} />
      </Route>
      <Route path="*" element={<NotFound />} />
    </Routes>
  );
}
