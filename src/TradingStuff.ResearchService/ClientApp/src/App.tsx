import { useState, useEffect } from 'react';
import Coverage from './components/Coverage';
import Backfill from './components/Backfill';
import Study from './components/Study';
import Automation from './components/Automation';
import OptionChains from './components/OptionChains';

type Page = 'coverage' | 'backfill' | 'study' | 'automation' | 'options';

function App() {
  const [currentPage, setCurrentPage] = useState<Page>('coverage');

  useEffect(() => {
    const pageFor = (pathname: string): Page => {
      if (pathname.includes('/automation')) return 'automation';
      if (pathname.includes('/options')) return 'options';
      if (pathname.includes('/study')) return 'study';
      if (pathname.includes('/backfill')) return 'backfill';
      return 'coverage';
    };

    // Determine which page to show based on pathname
    setCurrentPage(pageFor(window.location.pathname));

    // Listen for navigation events to switch pages
    const handlePopState = () => setCurrentPage(pageFor(window.location.pathname));

    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  const navigateTo = (page: Page) => {
    window.history.pushState(null, '', `/ui/${page}`);
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
          <button
            className={`nav-link ${currentPage === 'study' ? 'active' : ''}`}
            onClick={() => navigateTo('study')}
          >
            Study
          </button>
          <button
            className={`nav-link ${currentPage === 'automation' ? 'active' : ''}`}
            onClick={() => navigateTo('automation')}
          >
            Automation
          </button>
          <button
            className={`nav-link ${currentPage === 'options' ? 'active' : ''}`}
            onClick={() => navigateTo('options')}
          >
            Option Chains
          </button>
        </div>
      </nav>
      <main>
        {currentPage === 'coverage' && <Coverage />}
        {currentPage === 'backfill' && <Backfill />}
        {currentPage === 'study' && <Study />}
        {currentPage === 'automation' && <Automation />}
        {currentPage === 'options' && <OptionChains />}
      </main>
    </>
  );
}

export default App;
