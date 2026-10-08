import numpy as np
Zt=np.array([[1.0,0.25,-0.5,0.6,-1.0],[0.0,0.75,1.0,-0.4,0.3],[-0.5,0.5,0.0,0.2,0.7]])
M=Zt@Zt.T/5
A=np.array([[0.1,0.4,-0.2],[-0.4,0.0,0.3],[0.2,-0.3,-0.1]])
R=A@M+M@A.T
print(M); print("loss",repr((R*R).sum()))
print("dA",2*(2*R@M))  # d/dA sum R^2 = 2R : dR ; dR = dA M + M dA^T -> grad = 2R M + 2 R^T M = 4 R M (R sym)
