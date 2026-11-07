import { useEffect, useState } from 'react';

export default function Reports() {
  const [rows, setRows] = useState([]);

  useEffect(() => {
    fetch('/api/reports/sales?year=2025').then((r) => r.json()).then(setRows);
  }, []);

  return (
    <section>
      <h1>Rapporter</h1>
      <p>{rows.length} rader</p>
    </section>
  );
}
