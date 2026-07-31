import { useState, useEffect } from 'react';
import Coverage from './components/Coverage';
import Backfill from './components/Backfill';

function App() {
  const [currentPage, setCurrentPage] = useState<'coverage' | 'backfill'>('coverage');

  useEffect(() => {
    // Determine which page to show based on pathname
    const pathname = window.location.pathname;
    if (pathname.includes('/backfill')) {
      setCurrentPage('backfill');
    } else {
      setCurrentPage('coverage');
    }

    // Listen for navigation events to switch pages
    const handlePopState = () => {
      const newPathname = window.location.pathname;
      if (newPathname.includes('/backfill')) {
        setCurrentPage('backfill');
      } else {
        setCurrentPage('coverage');
      }
    };

    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  const navigateTo = (page: 'coverage' | 'backfill') => {
    const newPath = page === 'coverage' ? '/ui/coverage' : '/ui/backfill';
    window.history.pushState(null, '', newPath);
    setCurrentPage(page);
  };

  return (
    <>
      <nav className="app-nav">
        <div className="nav-links">
          <button
            className={`nav-link ${currentPage === 'coverage' ? 'active' : ''}`}
            onClick={() => navigateTo('coverage')}
          >
            Coverage
          </button>
          <button
            className={`nav-link ${currentPage === 'backfill' ? 'active' : ''}`}
            onClick={() => navigateTo('backfill')}
          >
            Backfill
          </button>
        </div>
      </nav>
      <main>
        {currentPage === 'coverage' && <Coverage />}
        {currentPage === 'backfill' && <Backfill />}
      </main>
    </>
  );
}

export default App;
