// ==UserScript==
// @name         新闻图片转PDF
// @namespace    http://tampermonkey.net/
// @version      1.5
// @description  从newsTitle获取标题，将newsContent内的所有图片转为每页一张的PDF
// @author       Assistant
// @match        *://*/*
// @grant        GM_download
// @grant        GM_xmlhttpRequest
// @require      https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js
// ==/UserScript==

(function() {
    'use strict';

    // 加载 html2canvas
    function loadHtml2canvas() {
        return new Promise((resolve, reject) => {
            if (typeof html2canvas !== 'undefined') {
                resolve();
                return;
            }
            
            const script = document.createElement('script');
            script.src = 'https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js';
            script.onload = () => {
                setTimeout(resolve, 300);
            };
            script.onerror = () => {
                reject(new Error('加载 html2canvas 失败'));
            };
            document.head.appendChild(script);
        });
    }

    // 显示状态
    function showStatus(message, isError = false) {
        let statusDiv = document.getElementById('pdf-status-indicator');
        if (!statusDiv) {
            statusDiv = document.createElement('div');
            statusDiv.id = 'pdf-status-indicator';
            statusDiv.style.cssText = `
                position: fixed;
                bottom: 100px;
                right: 30px;
                z-index: 99999;
                padding: 12px 24px;
                background: rgba(0, 0, 0, 0.85);
                color: white;
                border-radius: 10px;
                font-size: 15px;
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                backdrop-filter: blur(10px);
                box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
                transition: opacity 0.3s ease;
                max-width: 400px;
                pointer-events: none;
            `;
            document.body.appendChild(statusDiv);
        }
        
        statusDiv.textContent = message;
        statusDiv.style.opacity = '1';
        statusDiv.style.background = isError ? 'rgba(220, 50, 50, 0.9)' : 'rgba(0, 0, 0, 0.85)';
        
        clearTimeout(statusDiv._timeout);
        if (!isError) {
            statusDiv._timeout = setTimeout(() => {
                statusDiv.style.opacity = '0';
            }, 8000);
        }
    }

    // 创建按钮
    function createButton() {
        const button = document.createElement('button');
        button.textContent = '📄 导出PDF';
        button.style.cssText = `
            position: fixed;
            bottom: 30px;
            right: 30px;
            z-index: 99999;
            padding: 14px 28px;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border: none;
            border-radius: 50px;
            font-size: 18px;
            font-weight: bold;
            cursor: pointer;
            box-shadow: 0 8px 25px rgba(102, 126, 234, 0.5);
            transition: all 0.3s ease;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            letter-spacing: 0.5px;
        `;
        
        button.addEventListener('mouseenter', () => {
            button.style.transform = 'scale(1.05)';
            button.style.boxShadow = '0 12px 35px rgba(102, 126, 234, 0.6)';
        });
        button.addEventListener('mouseleave', () => {
            button.style.transform = 'scale(1)';
            button.style.boxShadow = '0 8px 25px rgba(102, 126, 234, 0.5)';
        });
        
        button.addEventListener('click', generatePDF);
        return button;
    }

    // 从图片获取数据URL（使用XMLHttpRequest绕过跨域）
    function getImageDataURL(img) {
        return new Promise((resolve) => {
            try {
                let src = img.src;
                
                // 如果是相对路径，转为绝对路径
                if (src.startsWith('//')) {
                    src = 'https:' + src;
                }
                if (!src.startsWith('http://') && !src.startsWith('https://') && !src.startsWith('data:')) {
                    src = window.location.origin + '/' + src.replace(/^\/+/, '');
                }
                
                // 如果已经是data URL，直接返回
                if (src.startsWith('data:')) {
                    resolve(src);
                    return;
                }
                
                // 使用GM_xmlhttpRequest获取图片数据
                GM_xmlhttpRequest({
                    method: 'GET',
                    url: src,
                    responseType: 'blob',
                    onload: function(response) {
                        try {
                            const reader = new FileReader();
                            reader.onloadend = function() {
                                resolve(reader.result);
                            };
                            reader.onerror = function() {
                                resolve(null);
                            };
                            reader.readAsDataURL(response.response);
                        } catch (e) {
                            resolve(null);
                        }
                    },
                    onerror: function() {
                        resolve(null);
                    }
                });
            } catch (e) {
                resolve(null);
            }
        });
    }

    // 使用html2canvas渲染图片
    async function renderWithHtml2canvas(img) {
        try {
            if (typeof html2canvas === 'undefined') {
                return null;
            }
            
            const wrapper = document.createElement('div');
            wrapper.style.cssText = `
                position: fixed;
                left: -9999px;
                top: 0;
                width: 800px;
                height: 600px;
                display: flex;
                align-items: center;
                justify-content: center;
                background: #ffffff;
                overflow: hidden;
            `;
            
            const cloneImg = document.createElement('img');
            cloneImg.src = img.src;
            cloneImg.style.cssText = 'max-width: 100%; max-height: 100%; object-fit: contain;';
            cloneImg.crossOrigin = 'anonymous';
            
            wrapper.appendChild(cloneImg);
            document.body.appendChild(wrapper);
            
            // 等待图片加载
            await new Promise((resolve) => {
                if (cloneImg.complete && cloneImg.naturalWidth > 0) {
                    resolve();
                } else {
                    cloneImg.onload = resolve;
                    cloneImg.onerror = resolve;
                    setTimeout(resolve, 5000);
                }
            });
            
            const canvas = await html2canvas(wrapper, {
                useCORS: true,
                scale: 2,
                backgroundColor: '#ffffff',
                allowTaint: true,
                logging: false,
                width: 800,
                height: 600
            });
            
            document.body.removeChild(wrapper);
            return canvas;
        } catch (e) {
            return null;
        }
    }

    // 主函数
    async function generatePDF() {
        const button = document.querySelector('#pdf-export-button');
        if (button) {
            button.disabled = true;
            button.textContent = '⏳ 处理中...';
            button.style.opacity = '0.6';
        }

        try {
            // 加载html2canvas
            await loadHtml2canvas();
            
            // 获取标题和内容元素
            const titleElement = document.getElementById('newsTitle');
            const contentElement = document.getElementById('newsContent');

            if (!titleElement || !contentElement) {
                showStatus('❌ 未找到 newsTitle 或 newsContent', true);
                return;
            }

            // 提取文件名
            let fileName = titleElement.textContent.trim() || 'document';
            fileName = fileName.replace(/[\\/:*?"<>|]/g, '_').substring(0, 200);

            // 获取所有图片
            const images = contentElement.querySelectorAll('img');
            if (images.length === 0) {
                showStatus('❌ 没有找到图片', true);
                return;
            }

            showStatus(`📸 找到 ${images.length} 张图片，正在处理...`);

            // 存储图片数据
            const imageDataList = [];
            
            for (let i = 0; i < images.length; i++) {
                const img = images[i];
                showStatus(`⏳ 处理图片 ${i+1}/${images.length}...`);
                
                let dataURL = null;
                let canvas = null;
                
                // 方法1: 使用GM_xmlhttpRequest获取图片数据
                dataURL = await getImageDataURL(img);
                
                // 方法2: 如果方法1失败，尝试使用canvas绘制
                if (!dataURL) {
                    try {
                        const tempCanvas = document.createElement('canvas');
                        const ctx = tempCanvas.getContext('2d');
                        
                        // 创建临时图片并绘制
                        const tempImg = new Image();
                        tempImg.crossOrigin = 'anonymous';
                        let src = img.src;
                        if (src.startsWith('//')) {
                            src = 'https:' + src;
                        }
                        if (!src.startsWith('http') && !src.startsWith('data:')) {
                            src = window.location.origin + '/' + src.replace(/^\/+/, '');
                        }
                        tempImg.src = src;
                        
                        await new Promise((resolve) => {
                            tempImg.onload = resolve;
                            tempImg.onerror = resolve;
                            setTimeout(resolve, 5000);
                        });
                        
                        if (tempImg.complete && tempImg.naturalWidth > 0) {
                            tempCanvas.width = tempImg.naturalWidth;
                            tempCanvas.height = tempImg.naturalHeight;
                            ctx.drawImage(tempImg, 0, 0);
                            dataURL = tempCanvas.toDataURL('image/jpeg', 0.95);
                        }
                    } catch (e) {
                        console.warn('Canvas绘制失败:', e);
                    }
                }
                
                // 方法3: 使用html2canvas
                if (!dataURL && typeof html2canvas !== 'undefined') {
                    canvas = await renderWithHtml2canvas(img);
                    if (canvas) {
                        try {
                            dataURL = canvas.toDataURL('image/jpeg', 0.95);
                        } catch (e) {
                            console.warn('html2canvas toDataURL失败:', e);
                        }
                    }
                }
                
                // 如果获取到数据，保存
                if (dataURL) {
                    imageDataList.push(dataURL);
                } else {
                    console.warn('图片处理失败:', img.src);
                }
            }

            if (imageDataList.length === 0) {
                showStatus('❌ 没有可用的图片', true);
                return;
            }

            showStatus(`✅ 成功处理 ${imageDataList.length} 张图片，生成PDF...`);

            // 创建PDF
            const { jsPDF } = window.jspdf;
            
            // 获取第一张图片的尺寸作为参考
            const firstImg = new Image();
            firstImg.src = imageDataList[0];
            await new Promise((resolve) => {
                firstImg.onload = resolve;
                firstImg.onerror = resolve;
                setTimeout(resolve, 3000);
            });
            
            // 计算PDF尺寸
            let pdfWidth = firstImg.width || 800;
            let pdfHeight = firstImg.height || 600;
            
            // 限制最大尺寸
            const maxDimension = 1200;
            if (pdfWidth > maxDimension || pdfHeight > maxDimension) {
                const scale = Math.min(maxDimension / pdfWidth, maxDimension / pdfHeight);
                pdfWidth = Math.round(pdfWidth * scale);
                pdfHeight = Math.round(pdfHeight * scale);
            }

            // 创建PDF
            const pdf = new jsPDF({
                orientation: pdfWidth > pdfHeight ? 'landscape' : 'portrait',
                unit: 'px',
                format: [pdfWidth, pdfHeight],
                compress: true
            });

            // 添加所有图片
            for (let i = 0; i < imageDataList.length; i++) {
                if (i > 0) {
                    pdf.addPage([pdfWidth, pdfHeight]);
                }
                
                const imgData = imageDataList[i];
                
                // 如果图片尺寸与PDF尺寸不同，需要计算适配
                const tempImg = new Image();
                tempImg.src = imgData;
                await new Promise((resolve) => {
                    tempImg.onload = resolve;
                    tempImg.onerror = resolve;
                    setTimeout(resolve, 3000);
                });
                
                if (tempImg.width && tempImg.height) {
                    // 计算缩放比例，使图片完整显示在页面中
                    const scaleX = pdfWidth / tempImg.width;
                    const scaleY = pdfHeight / tempImg.height;
                    const scale = Math.min(scaleX, scaleY);
                    
                    const finalWidth = Math.round(tempImg.width * scale);
                    const finalHeight = Math.round(tempImg.height * scale);
                    const offsetX = Math.round((pdfWidth - finalWidth) / 2);
                    const offsetY = Math.round((pdfHeight - finalHeight) / 2);
                    
                    pdf.addImage(imgData, 'JPEG', offsetX, offsetY, finalWidth, finalHeight);
                } else {
                    pdf.addImage(imgData, 'JPEG', 0, 0, pdfWidth, pdfHeight);
                }
            }

            // 下载PDF
            const pdfBlob = pdf.output('blob');
            const url = URL.createObjectURL(pdfBlob);
            
            try {
                GM_download({
                    url: url,
                    name: `${fileName}.pdf`,
                    saveAs: true
                });
                showStatus(`✅ PDF生成成功！共 ${imageDataList.length} 页`);
            } catch (e) {
                const link = document.createElement('a');
                link.href = url;
                link.download = `${fileName}.pdf`;
                document.body.appendChild(link);
                link.click();
                document.body.removeChild(link);
                showStatus(`✅ PDF生成成功！共 ${imageDataList.length} 页`);
            }
            
            setTimeout(() => URL.revokeObjectURL(url), 5000);

        } catch (error) {
            console.error('生成PDF失败:', error);
            showStatus(`❌ 生成失败: ${error.message || '未知错误'}`, true);
        } finally {
            const button = document.querySelector('#pdf-export-button');
            if (button) {
                button.disabled = false;
                button.textContent = '📄 导出PDF';
                button.style.opacity = '1';
            }
        }
    }

    // 添加按钮
    window.addEventListener('load', function() {
        setTimeout(() => {
            if (!document.getElementById('pdf-export-button')) {
                const button = createButton();
                button.id = 'pdf-export-button';
                document.body.appendChild(button);
            }
        }, 1000);
    });

})();