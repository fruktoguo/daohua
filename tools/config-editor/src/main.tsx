// 入口：挂载 React 根节点并启用 StrictMode（开发期双重渲染暴露副作用问题）
import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
